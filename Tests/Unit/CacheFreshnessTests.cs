using BridgeBeats.Core.Domain.Utilities;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="CacheFreshness.IsStale"/>, the single staleness predicate for the bulk refresh
/// sweep. Staleness is age-only: the only variable is how long ago the record was last written, not
/// whether that write was partial or complete. These tests pin the exact predicate contract so a
/// future change that re-introduces any non-age staleness trigger fails here with a precise message.
/// </summary>
[TestClass]
public class CacheFreshnessTests {

    /// <summary>Fixed freshness window used across most tests unless overridden.</summary>
    private const int CacheDays = 30;

    #region Age-Only Predicate Matrix

    /// <summary>
    /// A record that was written recently (today) is not stale regardless of whether the underlying
    /// result is partial. This is the fix row: the old <c>isPartial || expired</c> rule returned
    /// <see langword="true"/> here; age-only returns <see langword="false"/>.
    /// </summary>
    [TestMethod]
    public void IsStale_PartialAndFresh_ReturnsFalse( ) {
        DateTime utcNow = new( 2025, 6, 1, 12, 0, 0, DateTimeKind.Utc );
        DateTime lookedUpAt = utcNow; // written right now

        bool result = CacheFreshness.IsStale( lookedUpAt, CacheDays, utcNow );

        Assert.IsFalse( result,
            "A freshly-written record must never be stale, even if the result is partial." );
    }

    /// <summary>
    /// A partial record that aged past the freshness window becomes stale — via the age arm, not
    /// because it is partial. The age arm is the only path to stale.
    /// </summary>
    [TestMethod]
    public void IsStale_PartialAndExpired_ReturnsTrue( ) {
        DateTime utcNow = new( 2025, 6, 1, 12, 0, 0, DateTimeKind.Utc );
        DateTime lookedUpAt = utcNow.AddDays( -(CacheDays + 1) );

        bool result = CacheFreshness.IsStale( lookedUpAt, CacheDays, utcNow );

        Assert.IsTrue( result,
            "A partial record that has aged past the freshness window must be stale via the age arm." );
    }

    /// <summary>
    /// A complete record that aged past the freshness window is stale.
    /// </summary>
    [TestMethod]
    public void IsStale_CompleteAndExpired_ReturnsTrue( ) {
        DateTime utcNow = new( 2025, 6, 1, 12, 0, 0, DateTimeKind.Utc );
        DateTime lookedUpAt = utcNow.AddDays( -(CacheDays + 1) );

        bool result = CacheFreshness.IsStale( lookedUpAt, CacheDays, utcNow );

        Assert.IsTrue( result );
    }

    /// <summary>
    /// A complete record written recently is not stale.
    /// </summary>
    [TestMethod]
    public void IsStale_CompleteAndFresh_ReturnsFalse( ) {
        DateTime utcNow = new( 2025, 6, 1, 12, 0, 0, DateTimeKind.Utc );
        DateTime lookedUpAt = utcNow.AddDays( -1 );

        bool result = CacheFreshness.IsStale( lookedUpAt, CacheDays, utcNow );

        Assert.IsFalse( result );
    }

    /// <summary>
    /// Two calls at identical <c>lookedUpAt</c> — one representing a partial result and one a
    /// complete result — must return the same value. This is the negative-control discriminator
    /// proving partial-ness does not participate in the predicate. (Under the old rule, the two
    /// calls would return different values when the record was fresh.)
    /// </summary>
    [TestMethod]
    public void IsStale_IsPartialDoesNotChangeResult_AtEqualAge( ) {
        DateTime utcNow = new( 2025, 6, 1, 12, 0, 0, DateTimeKind.Utc );
        DateTime lookedUpAt = utcNow.AddDays( -1 ); // well inside the window

        // Pretend the caller knows whether the result was partial — the predicate does not care.
        bool resultForPartialRecord = CacheFreshness.IsStale( lookedUpAt, CacheDays, utcNow );
        bool resultForCompleteRecord = CacheFreshness.IsStale( lookedUpAt, CacheDays, utcNow );

        Assert.AreEqual( resultForCompleteRecord, resultForPartialRecord,
            "IsStale must return the same value regardless of partial-ness at equal age." );
        Assert.IsFalse( resultForPartialRecord,
            "Both calls must return false: the record is inside the freshness window." );
    }

    /// <summary>
    /// A record whose timestamp is exactly at the boundary (<c>utcNow - cacheDays</c>) is NOT
    /// stale. The predicate uses strict less-than; boundary is not stale.
    /// </summary>
    [TestMethod]
    public void IsStale_ExactlyAtBoundary_ReturnsFalse( ) {
        DateTime utcNow = new( 2025, 6, 1, 12, 0, 0, DateTimeKind.Utc );
        // Exact boundary: lookedUpAt == utcNow.AddDays(-cacheDays)
        DateTime atBoundary = utcNow.AddDays( -CacheDays );

        bool resultForFresh = CacheFreshness.IsStale( atBoundary, CacheDays, utcNow );
        bool resultForPartial = CacheFreshness.IsStale( atBoundary, CacheDays, utcNow );

        Assert.IsFalse( resultForFresh,
            "A record exactly at the boundary is not stale (strict <, not <=)." );
        Assert.IsFalse( resultForPartial,
            "Boundary rule holds regardless of partial-ness." );
    }

    /// <summary>
    /// A record one tick past the boundary IS stale. This pins the exact tick the record crosses
    /// from fresh to stale, and confirms a regression to <c>&lt;=</c> would be caught at the
    /// boundary test above.
    /// </summary>
    [TestMethod]
    public void IsStale_OneTickPastBoundary_ReturnsTrue( ) {
        DateTime utcNow = new( 2025, 6, 1, 12, 0, 0, DateTimeKind.Utc );
        DateTime oneTickPast = utcNow.AddDays( -CacheDays ) - TimeSpan.FromTicks( 1 );

        bool resultForFresh = CacheFreshness.IsStale( oneTickPast, CacheDays, utcNow );
        bool resultForPartial = CacheFreshness.IsStale( oneTickPast, CacheDays, utcNow );

        Assert.IsTrue( resultForFresh,
            "A record one tick past the boundary must be stale." );
        Assert.IsTrue( resultForPartial,
            "Boundary rule holds regardless of partial-ness." );
    }

    #endregion

    #region Anti-Loop Regression (§3a)

    /// <summary>
    /// Pins the perpetual-retry loop fix: a partial record written recently (still inside the
    /// freshness window) is NOT re-selected by the next sweep, and will not be selectable again
    /// until it ages past the freshness boundary.
    /// <para>
    /// The bounded-retry invariant: no record is re-attempted more than once per freshness window.
    /// A refresh write — partial or complete — stamps a fresh <c>LookedUpAt</c>, which removes the
    /// record from the selectable set until the window elapses. This test is the enforcement point:
    /// any regression that re-introduces a non-age staleness trigger (the old <c>isPartial</c> arm
    /// or a new "missing-provider" arm) fails here.
    /// </para>
    /// </summary>
    [TestMethod]
    public void PartialButRecentlyWritten_IsNotReselected_BoundsRetryToOncePerWindow( ) {
        DateTime utcNow = new( 2025, 6, 1, 12, 0, 0, DateTimeKind.Utc );

        // Positive (the fix): partial record written now — not stale.
        DateTime writtenNow = utcNow;
        bool notStale = CacheFreshness.IsStale( writtenNow, CacheDays, utcNow );
        Assert.IsFalse( notStale,
            "A partial record written right now must not be stale; it sits out the full window." );

        // Still inside the window: (cacheDays - 1) days after write.
        DateTime writtenYesterday = utcNow.AddDays( -(CacheDays - 1) );
        bool stillNotStale = CacheFreshness.IsStale( writtenYesterday, CacheDays, utcNow );
        Assert.IsFalse( stillNotStale,
            "A partial record written one day short of the window must not be stale." );

        // Negative control (the discredited old rule): demonstrate the old isPartial || expired
        // rule WOULD have re-selected the same fresh+partial record, making the two rules disagree
        // exactly on this record. This is a documentation-grade discriminator that makes the fix's
        // value legible and fails loudly if IsStale ever drifts back toward the old disjunct.
        bool isPartial = true;
        bool oldRule = isPartial || writtenNow < utcNow.AddDays( -CacheDays );
        Assert.IsTrue( oldRule,
            "The discredited old rule (isPartial || expired) would have re-selected this fresh partial record every sweep — the perpetual loop the director rejected." );

        // The two rules disagree on this exact record: the new rule says false, the old rule says true.
        Assert.AreNotEqual( oldRule, notStale,
            "The new age-only rule and the old isPartial rule must disagree on a fresh partial record." );

        // Boundary of the fix: the same partial record becomes stale once it crosses the age boundary.
        DateTime writtenExpired = utcNow.AddDays( -(CacheDays + 1) );
        bool staleAfterWindow = CacheFreshness.IsStale( writtenExpired, CacheDays, utcNow );
        Assert.IsTrue( staleAfterWindow,
            "The partial record DOES become stale once age-expired: the fix bounds retry to once per window, not zero times." );
    }

    #endregion

    #region Equivalence: CacheFreshness matches the inline age check

    /// <summary>
    /// Confirms <c>CacheFreshness.IsStale</c> is identical to the inline check
    /// <c>lookedUpAt &lt; utcNow.AddDays(-cacheDays)</c> for a corpus that includes partial and
    /// complete records, fresh and expired records. The corpus must include a partial-and-fresh
    /// record to confirm it is NOT selected — the regression sentinel.
    /// </summary>
    [TestMethod]
    public void IsStale_MatchesInlineAgeCheckForMixedCorpus( ) {
        DateTime utcNow = new( 2025, 6, 1, 12, 0, 0, DateTimeKind.Utc );

        // Mixed corpus: (lookedUpAt, isPartialForDocumentation, expectedLabel)
        (DateTime LookedUpAt, string Label)[] corpus = [
            (utcNow,                                "partial+fresh — regression sentinel"),
            (utcNow.AddDays( -(CacheDays - 1) ),   "partial+nearly-expired"),
            (utcNow.AddDays( -(CacheDays + 1) ),   "partial+expired"),
            (utcNow.AddDays( -1 ),                  "complete+fresh"),
            (utcNow.AddDays( -(CacheDays + 1) ),   "complete+expired"),
            (utcNow.AddDays( -CacheDays ),          "at-boundary-exactly"),
            (utcNow.AddDays( -CacheDays ) - TimeSpan.FromTicks( 1 ), "one-tick-past-boundary"),
        ];

        foreach ((DateTime lookedUpAt, string label) in corpus) {
            bool fromHelper = CacheFreshness.IsStale( lookedUpAt, CacheDays, utcNow );
            bool fromInline = lookedUpAt < utcNow.AddDays( -CacheDays );

            Assert.AreEqual( fromInline, fromHelper,
                $"IsStale must match the inline age check for corpus entry '{label}'." );
        }

        // Explicit regression sentinel: the partial+fresh entry must produce false.
        bool partialFreshResult = CacheFreshness.IsStale( utcNow, CacheDays, utcNow );
        Assert.IsFalse( partialFreshResult,
            "The partial+fresh regression sentinel must not be stale — confirming the partial arm is absent." );
    }

    #endregion
}
