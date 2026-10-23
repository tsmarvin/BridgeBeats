using System.ComponentModel.DataAnnotations;
using BridgeBeats.Web.Controllers;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for the data-annotation validation attributes on
/// <see cref="PlaylistController.CreatePlaylistRequest"/>.
/// Covers the <c>[StringLength]</c> constraints added in SEC-E-001 as
/// defense-in-depth to bound user-controlled input before it reaches the
/// view-layer data-title sinks.
/// </summary>
[TestClass]
public class CreatePlaylistRequestValidationTests {

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static IList<ValidationResult> Validate( PlaylistController.CreatePlaylistRequest request ) {
        var context = new ValidationContext( request );
        var results = new List<ValidationResult>( );
        _ = Validator.TryValidateObject( request, context, results, validateAllProperties: true );
        return results;
    }

    private static bool IsValid( PlaylistController.CreatePlaylistRequest request ) =>
        Validator.TryValidateObject( request, new ValidationContext( request ), null, validateAllProperties: true );

    // -----------------------------------------------------------------------
    // Title — valid values
    // -----------------------------------------------------------------------

    /// <summary>
    /// A <see langword="null"/> title must pass validation; the field is optional.
    /// </summary>
    [TestMethod]
    public void Title_Null_PassesValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Title = null };
        Assert.IsTrue( IsValid( request ) );
    }

    /// <summary>
    /// A title of exactly 1 character satisfies the <c>MinimumLength = 1</c> constraint.
    /// </summary>
    [TestMethod]
    public void Title_SingleCharacter_PassesValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Title = "A" };
        Assert.IsTrue( IsValid( request ) );
    }

    /// <summary>
    /// A title of exactly 200 characters satisfies the <c>MaximumLength = 200</c> constraint.
    /// </summary>
    [TestMethod]
    public void Title_ExactlyMaxLength_PassesValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Title = new string( 'a', 200 ) };
        Assert.IsTrue( IsValid( request ) );
    }

    // -----------------------------------------------------------------------
    // Title — invalid values (negative controls)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Negative control: an empty string title must fail validation (<c>MinimumLength = 1</c>).
    /// </summary>
    [TestMethod]
    public void Title_EmptyString_FailsValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Title = string.Empty };
        IList<ValidationResult> results = Validate( request );
        Assert.IsNotEmpty( results );
    }

    /// <summary>
    /// Negative control: a title of 201 characters must fail validation (<c>MaximumLength = 200</c>).
    /// </summary>
    [TestMethod]
    public void Title_OneOverMaxLength_FailsValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Title = new string( 'a', 201 ) };
        IList<ValidationResult> results = Validate( request );
        Assert.IsNotEmpty( results );
    }

    /// <summary>
    /// Negative control: a very long title (1000 characters) must fail validation.
    /// </summary>
    [TestMethod]
    public void Title_VeryLongString_FailsValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Title = new string( 'x', 1000 ) };
        IList<ValidationResult> results = Validate( request );
        Assert.IsNotEmpty( results );
    }

    // -----------------------------------------------------------------------
    // Description — valid values
    // -----------------------------------------------------------------------

    /// <summary>
    /// A <see langword="null"/> description must pass validation; the field is optional.
    /// </summary>
    [TestMethod]
    public void Description_Null_PassesValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Description = null };
        Assert.IsTrue( IsValid( request ) );
    }

    /// <summary>
    /// A description of exactly 1000 characters satisfies the <c>MaximumLength = 1000</c> constraint.
    /// </summary>
    [TestMethod]
    public void Description_ExactlyMaxLength_PassesValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Description = new string( 'd', 1000 ) };
        Assert.IsTrue( IsValid( request ) );
    }

    // -----------------------------------------------------------------------
    // Description — invalid values (negative controls)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Negative control: an empty string description must fail validation (<c>MinimumLength = 1</c>).
    /// </summary>
    [TestMethod]
    public void Description_EmptyString_FailsValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Description = string.Empty };
        IList<ValidationResult> results = Validate( request );
        Assert.IsNotEmpty( results );
    }

    /// <summary>
    /// Negative control: a description of 1001 characters must fail validation (<c>MaximumLength = 1000</c>).
    /// </summary>
    [TestMethod]
    public void Description_OneOverMaxLength_FailsValidation( ) {
        var request = new PlaylistController.CreatePlaylistRequest { Description = new string( 'd', 1001 ) };
        IList<ValidationResult> results = Validate( request );
        Assert.IsNotEmpty( results );
    }
}
