using System.Net;
using BridgeBeats.Worker.Discord;
using NetCord.Rest;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests the Discord message handler's treatment of the original-message deletion. Deleting the original
/// message after posting a card is optional channel cleanup, so the Discord 403 "Missing Permissions"
/// response returned when the bot was not granted Manage Messages is an expected outcome rather than an
/// error. Covers the classifier for that specific response, the fact that deletion is attempted and its
/// outcome absorbed, and that every other failure still propagates. The 403/50013 pairing asserted here
/// mirrors the response recorded in the worker's production logs alongside
/// <c>MessageCreateGatewayHandler</c>.
/// </summary>
[TestClass]
public class DiscordMessageDeletePermissionTests {

    /// <summary>The Discord API error code for "Missing Permissions", as returned in the response body.</summary>
    private const int MissingPermissionsCode = 50013;

    /// <summary>
    /// Builds the exception shape Discord returns for a missing-permission failure: HTTP 403 with a JSON
    /// body carrying <c>code</c> 50013. NetCord parses that body into <see cref="RestException.Error"/>.
    /// </summary>
    /// <param name="statusCode">The HTTP status to report.</param>
    /// <param name="errorCode">The Discord error code to report in the response body.</param>
    /// <param name="message">The error message to report in the response body.</param>
    /// <returns>The constructed exception.</returns>
    private static RestException BuildRestException( HttpStatusCode statusCode, int errorCode, string message )
        => new( statusCode, "Forbidden", new RestError( errorCode, message, null ) );

    #region Response classification

    /// <summary>
    /// A 403 carrying error code 50013 is the missing-permissions response and is recognized as such.
    /// </summary>
    [TestMethod]
    public void IsMissingPermissions_ReturnsTrue_ForForbiddenWithMissingPermissionsCode( ) {
        // Arrange - matches the production response: 403 (Forbidden). Missing Permissions.
        RestException exception = BuildRestException( HttpStatusCode.Forbidden, MissingPermissionsCode, "Missing Permissions" );

        // Act
        bool result = MessageCreateGatewayHandler.IsMissingPermissions( exception );

        // Assert
        Assert.IsTrue( result );
    }

    /// <summary>
    /// A 403 with no parsed error body is not the missing-permissions response; without the body there is no
    /// evidence that the failure was caused by a missing permission rather than a rejected token.
    /// </summary>
    [TestMethod]
    public void IsMissingPermissions_ReturnsFalse_ForForbiddenWithoutErrorBody( ) {
        // Arrange
        RestException exception = new( HttpStatusCode.Forbidden, "Forbidden" );

        // Act
        bool result = MessageCreateGatewayHandler.IsMissingPermissions( exception );

        // Assert
        Assert.IsFalse( result );
    }

    /// <summary>
    /// A 403 carrying a different permission error code is not the missing-permissions response, so a
    /// distinct permission failure such as Missing Access is not absorbed alongside it.
    /// </summary>
    [TestMethod]
    public void IsMissingPermissions_ReturnsFalse_ForForbiddenWithDifferentErrorCode( ) {
        // Arrange - 50001 is Missing Access, a different permission failure
        RestException exception = BuildRestException( HttpStatusCode.Forbidden, 50001, "Missing Access" );

        // Act
        bool result = MessageCreateGatewayHandler.IsMissingPermissions( exception );

        // Assert
        Assert.IsFalse( result );
    }

    /// <summary>
    /// Error code 50013 reported under a non-403 status is not the missing-permissions response, so the
    /// classifier does not match on the body code alone.
    /// </summary>
    [TestMethod]
    public void IsMissingPermissions_ReturnsFalse_ForNonForbiddenStatus( ) {
        // Arrange
        RestException exception = BuildRestException( HttpStatusCode.Unauthorized, MissingPermissionsCode, "Missing Permissions" );

        // Act
        bool result = MessageCreateGatewayHandler.IsMissingPermissions( exception );

        // Assert
        Assert.IsFalse( result );
    }

    #endregion

    #region Deletion outcome

    /// <summary>
    /// A successful deletion completes without an exception and actually invokes the deletion.
    /// </summary>
    [TestMethod]
    public async Task DeleteOriginalMessageAsync_CompletesAndDeletes_WhenDeletionSucceeds( ) {
        // Arrange
        bool deleted = false;
        Task DeleteAsync( ) {
            deleted = true;
            return Task.CompletedTask;
        }

        // Act
        await MessageCreateGatewayHandler.DeleteOriginalMessageAsync( DeleteAsync );

        // Assert
        Assert.IsTrue( deleted );
    }

    /// <summary>
    /// The missing-permissions response for the deletion is absorbed: the deletion was attempted and the
    /// handler completes without an exception, leaving the reply already posted and the original message in
    /// place.
    /// </summary>
    [TestMethod]
    public async Task DeleteOriginalMessageAsync_AbsorbsMissingPermissionsResponse( ) {
        // Arrange
        bool attempted = false;
        RestException missingPermissions = BuildRestException( HttpStatusCode.Forbidden, MissingPermissionsCode, "Missing Permissions" );
        Task DeleteAsync( ) {
            attempted = true;
            throw missingPermissions;
        }

        // Act - must not throw
        await MessageCreateGatewayHandler.DeleteOriginalMessageAsync( DeleteAsync );

        // Assert
        Assert.IsTrue( attempted );
    }

    /// <summary>
    /// A REST failure other than missing permissions still propagates, so unrelated Discord errors are not
    /// silently treated as expected outcomes.
    /// </summary>
    [TestMethod]
    public async Task DeleteOriginalMessageAsync_PropagatesUnrelatedRestException( ) {
        // Arrange - 403 whose body reports a different permission error
        RestException missingAccess = BuildRestException( HttpStatusCode.Forbidden, 50001, "Missing Access" );
        Task DeleteAsync( ) => throw missingAccess;

        // Act - the deletion runs through the handler so its absorption guard is the code under test
        RestException caught = await Assert.ThrowsExactlyAsync<RestException>(
            ( ) => MessageCreateGatewayHandler.DeleteOriginalMessageAsync( DeleteAsync ) );

        // Assert
        Assert.AreSame( missingAccess, caught );
    }

    /// <summary>
    /// A REST failure reported without an error body still propagates, since the failure cannot be
    /// attributed to a missing permission.
    /// </summary>
    [TestMethod]
    public async Task DeleteOriginalMessageAsync_PropagatesRestExceptionWithoutErrorBody( ) {
        // Arrange
        RestException forbidden = new( HttpStatusCode.Forbidden, "Forbidden" );
        Task DeleteAsync( ) => throw forbidden;

        // Act - the deletion runs through the handler so its absorption guard is the code under test
        RestException caught = await Assert.ThrowsExactlyAsync<RestException>(
            ( ) => MessageCreateGatewayHandler.DeleteOriginalMessageAsync( DeleteAsync ) );

        // Assert
        Assert.AreSame( forbidden, caught );
    }

    /// <summary>
    /// A non-REST failure still propagates, so the absorption is limited to the single expected response.
    /// </summary>
    [TestMethod]
    public async Task DeleteOriginalMessageAsync_PropagatesNonRestException( ) {
        // Arrange
        Task DeleteAsync( ) => throw new InvalidOperationException( "delete failed" );

        // Act - the deletion runs through the handler so its absorption guard is the code under test
        InvalidOperationException caught = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            ( ) => MessageCreateGatewayHandler.DeleteOriginalMessageAsync( DeleteAsync ) );

        // Assert
        Assert.AreEqual( "delete failed", caught.Message );
    }

    #endregion
}
