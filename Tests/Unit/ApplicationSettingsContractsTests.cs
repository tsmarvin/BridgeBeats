using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Core.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>Tests redaction and validation behavior in the application-settings contracts.</summary>
[TestClass]
public class ApplicationSettingsContractsTests {
    private const string Secret = "super-sensitive-setting-value";
    private static readonly JsonSerializerOptions s_webJsonOptions = new( JsonSerializerDefaults.Web );

    /// <summary>Verifies runtime secret wrappers redact string and JSON display paths.</summary>
    [TestMethod]
    public void ApplicationSecretValue_DisplayPaths_DoNotRevealPlaintext( ) {
        ApplicationSecretValue value = ApplicationSecretValue.FromPlaintext( Secret );

        string json = JsonSerializer.Serialize( value );

        Assert.IsTrue( value.IsConfigured );
        Assert.AreEqual( Secret, value.Reveal( ) );
        Assert.DoesNotContain( Secret, value.ToString( ) );
        Assert.DoesNotContain( Secret, json );
        Assert.Contains( "IsConfigured", json );
    }

    /// <summary>Verifies replacement updates omit plaintext from their display representations.</summary>
    [TestMethod]
    public void ApplicationSecretUpdate_DisplayPaths_DoNotRevealReplacement( ) {
        ApplicationSecretUpdate update = ApplicationSecretUpdate.Replace( Secret );

        string json = JsonSerializer.Serialize( update );

        Assert.AreEqual( Secret, update.RevealReplacement( ) );
        Assert.DoesNotContain( Secret, update.ToString( ) );
        Assert.DoesNotContain( Secret, json );
        Assert.Contains( "Mode", json );
    }

    /// <summary>Verifies blank replacement values are rejected before persistence.</summary>
    [TestMethod]
    public void ApplicationSecretUpdate_BlankReplacement_Throws( ) {
        _ = Assert.ThrowsExactly<ArgumentException>( ( ) => ApplicationSecretUpdate.Replace( " " ) );
    }

    /// <summary>Verifies partial provider credential groups fail structural validation.</summary>
    [TestMethod]
    public void Validator_PartialProviderCredentials_ReturnsNonSensitiveError( ) {
        ApplicationSettingsValues values = new( ) { SpotifyClientId = Secret };

        IReadOnlyList<string> errors = ApplicationSettingsValidator.Validate(
            values,
            new StoredApplicationSettingsSecrets( )
        );

        Assert.HasCount( 1, errors );
        Assert.Contains( "Spotify", errors[0] );
        Assert.DoesNotContain( Secret, string.Join( ' ', errors ) );
    }

    /// <summary>Verifies a complete provider pair and default operational values are valid.</summary>
    [TestMethod]
    public void Validator_CompleteProviderCredentials_ReturnsNoErrors( ) {
        ApplicationSettingsValues values = new( ) { SpotifyClientId = "client-id" };
        StoredApplicationSettingsSecrets secrets = new( ) { SpotifyClientSecret = Secret };

        IReadOnlyList<string> errors = ApplicationSettingsValidator.Validate( values, secrets );

        Assert.IsEmpty( errors );
        Assert.DoesNotContain( Secret, secrets.ToString( ) );
    }

    /// <summary>Verifies real validator output and its exception never echo a supplied credential value.</summary>
    [TestMethod]
    public void ValidationException_Message_DoesNotIncludeValues( ) {
        ApplicationSettingsValues values = new( ) { SpotifyClientId = Secret };
        IReadOnlyList<string> errors = ApplicationSettingsValidator.Validate(
            values,
            new StoredApplicationSettingsSecrets( )
        );
        ApplicationSettingsValidationException exception = new( errors );

        Assert.AreEqual( "Application settings validation failed.", exception.Message );
        Assert.HasCount( 1, exception.Errors );
        Assert.DoesNotContain( Secret, exception.Message );
        Assert.DoesNotContain( Secret, string.Join( ' ', exception.Errors ) );
    }

    /// <summary>Verifies the service-account login, app password, and repository DID form one group.</summary>
    [TestMethod]
    public void Validator_ATProtoServiceAccount_RequiresCompleteValidGroup( ) {
        ApplicationSettingsValues partial = new( ) { ATProtoIdentifier = "service.example.com" };
        IReadOnlyList<string> partialErrors = ApplicationSettingsValidator.Validate(
            partial,
            new StoredApplicationSettingsSecrets( )
        );

        ApplicationSettingsValues malformedDid = partial with { ATProtoUserDid = "not-a-did" };
        IReadOnlyList<string> malformedErrors = ApplicationSettingsValidator.Validate(
            malformedDid,
            new StoredApplicationSettingsSecrets { ATProtoPassword = Secret }
        );

        ApplicationSettingsValues complete = partial with { ATProtoUserDid = "did:plc:bridgebeats" };
        IReadOnlyList<string> completeErrors = ApplicationSettingsValidator.Validate(
            complete,
            new StoredApplicationSettingsSecrets { ATProtoPassword = Secret }
        );

        Assert.Contains( error => error.Contains( "service-account", StringComparison.Ordinal ), partialErrors );
        Assert.Contains( error => error.Contains( "ATProtoUserDid", StringComparison.Ordinal ), malformedErrors );
        Assert.IsEmpty( completeErrors );
    }

    /// <summary>Verifies explicit JSON nulls are rejected as validation failures rather than dereferenced.</summary>
    [TestMethod]
    public void ValidateUpdateGraph_ExplicitNullMembers_ReturnsStructuralErrors( ) {
        ApplicationSettingsUpdate? update = JsonSerializer.Deserialize<ApplicationSettingsUpdate>(
            """{"values":{"queue":null,"resilience":null},"secrets":null}""",
            s_webJsonOptions
        );

        Assert.IsNotNull( update );
        IReadOnlyList<string> errors = ApplicationSettingsValidator.ValidateUpdateGraph( update );

        Assert.Contains( "Queue cannot be null.", errors );
        Assert.Contains( "Resilience cannot be null.", errors );
        Assert.Contains( "Secrets cannot be null.", errors );
    }

    /// <summary>Verifies database JSON names and provider dictionary keys remain stable.</summary>
    [TestMethod]
    public void ApplicationSettingsJson_PinsNamesAndNumericProviderKeys( ) {
        ApplicationSettingsValues values = new( ) {
            SpotifyClientId = "client-id",
            Queue = new QueueSettings {
                ProviderConcurrency = new Dictionary<SupportedProviders, int> {
                    [SupportedProviders.Spotify] = 3
                }
            }
        };
        JsonSerializerOptions options = ApplicationSettingsJson.CreateOptions( );

        string json = JsonSerializer.Serialize( values, options );
        ApplicationSettingsValues? roundTrip = JsonSerializer.Deserialize<ApplicationSettingsValues>( json, options );

        Assert.Contains( "\"spotifyClientId\"", json );
        Assert.Contains( "\"providerConcurrency\":{\"2\":3}", json );
        Assert.DoesNotContain( "\"Spotify\"", json );
        Assert.AreEqual( 3, roundTrip?.Queue.ProviderConcurrency[SupportedProviders.Spotify] );
    }

    /// <summary>Verifies complete runtime and update aggregates redact their nested secret values.</summary>
    [TestMethod]
    public void CompositeSettingsSurfaces_DoNotRevealPlaintext( ) {
        ApplicationSettingsSnapshot snapshot = new(
            new ApplicationSettingsValues( ),
            new ApplicationSettingsSecrets {
                SpotifyClientSecret = ApplicationSecretValue.FromPlaintext( Secret )
            },
            "revision",
            DateTimeOffset.UnixEpoch
        );
        ApplicationSettingsUpdate update = new( ) {
            Values = new ApplicationSettingsValues( ),
            Secrets = new ApplicationSettingsSecretUpdates {
                SpotifyClientSecret = ApplicationSecretUpdate.Replace( Secret )
            }
        };

        string surfaces = string.Join(
            '\n',
            snapshot.ToString( ),
            JsonSerializer.Serialize( snapshot ),
            update.ToString( ),
            JsonSerializer.Serialize( update )
        );

        Assert.DoesNotContain( Secret, surfaces );
    }

    /// <summary>Verifies display and trusted-runtime interfaces share one service without sharing contracts.</summary>
    [TestMethod]
    public void ServiceRegistration_SeparatesDisplayAndRuntimeInterfaces( ) {
        ServiceCollection services = new( );
        _ = services.AddSingleton( Mock.Of<IDbContextFactory<ApplicationDbContext>>( ) );
        _ = services.AddSingleton( TimeProvider.System );
        _ = services.AddDataProtection( );
        _ = services.AddDatabaseApplicationSettings( );
        using ServiceProvider provider = services.BuildServiceProvider( );

        IApplicationSettingsService displayService = provider.GetRequiredService<IApplicationSettingsService>( );
        IApplicationSettingsRuntimeReader runtimeReader =
            provider.GetRequiredService<IApplicationSettingsRuntimeReader>( );

        Assert.AreSame<object>( displayService, runtimeReader );
        Assert.IsNull( typeof( IApplicationSettingsService ).GetMethod( "GetRuntimeSettingsAsync" ) );
    }

    /// <summary>Verifies runtime service resolution cannot proceed without Data Protection.</summary>
    [TestMethod]
    public void ServiceRegistration_MissingDataProtectionProvider_FailsFast( ) {
        ServiceCollection services = new( );
        _ = services.AddSingleton( Mock.Of<IDbContextFactory<ApplicationDbContext>>( ) );
        _ = services.AddSingleton( TimeProvider.System );
        _ = services.AddDatabaseApplicationSettings( );
        using ServiceProvider provider = services.BuildServiceProvider( );

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            provider.GetRequiredService<IApplicationSettingsService>
        );

        Assert.Contains( nameof( IDataProtectionProvider ), exception.Message );
    }
}
