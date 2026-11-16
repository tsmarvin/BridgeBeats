using BridgeBeats.Contracts.Records;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Pins the computed <see cref="LookupResult.IsPartial"/> invariant directly. Covers the three
/// canonical cases: a non-empty <see cref="LookupResult.SagaId"/> (partial), no
/// <see cref="LookupResult.SagaId"/> (not partial), and an empty-string
/// <see cref="LookupResult.SagaId"/> (not partial). The empty-string case is the critical
/// regression guard: it distinguishes the hardened <c>!string.IsNullOrEmpty(SagaId)</c>
/// predicate from the prior <c>SagaId is not null</c> form.
/// </summary>
[TestClass]
public class LookupResultTests {

    /// <summary>
    /// <see cref="LookupResult.IsPartial"/> is <c>true</c> when <see cref="LookupResult.SagaId"/>
    /// is a non-empty string, indicating an in-progress saga is coordinating the lookup.
    /// </summary>
    [TestMethod]
    public void IsPartial_ReturnsTrueForNonEmptySagaId( ) {
        // Arrange
        LookupResult result = new( ) { SagaId = "saga-123" };

        // Assert
        Assert.IsTrue( result.IsPartial );
    }

    /// <summary>
    /// <see cref="LookupResult.IsPartial"/> is <c>false</c> when <see cref="LookupResult.SagaId"/>
    /// is <see langword="null"/> (default), indicating a final result with no saga in play.
    /// </summary>
    [TestMethod]
    public void IsPartial_ReturnsFalseWhenSagaIdIsNull( ) {
        // Arrange
        LookupResult result = new( );

        // Assert
        Assert.IsFalse( result.IsPartial );
    }

    /// <summary>
    /// <see cref="LookupResult.IsPartial"/> is <c>false</c> when <see cref="LookupResult.SagaId"/>
    /// is an empty string. This is the regression guard for the <c>!string.IsNullOrEmpty(SagaId)</c>
    /// predicate: the prior <c>SagaId is not null</c> form would have returned <c>true</c> for this
    /// input, treating an empty string as a valid saga identifier.
    /// </summary>
    [TestMethod]
    public void IsPartial_ReturnsFalseForEmptySagaId( ) {
        // Arrange
        LookupResult result = new( ) { SagaId = "" };

        // Assert
        Assert.IsFalse( result.IsPartial );
    }
}
