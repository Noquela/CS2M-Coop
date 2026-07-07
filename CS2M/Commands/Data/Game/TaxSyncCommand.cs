using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     Broadcast when any player changes a tax rate. Legacy shape (<c>Indices</c> null — the
    ///     default, gate <c>CS2M_TAXFIX=0</c>): <c>Rates</c> carries the WHOLE tax-rate array
    ///     (<c>TaxSystem.GetTaxRates()</c> — one int per index; the layout is deterministic for the same
    ///     game version, so plain indices are cross-PC stable). CONFIRMED gap with this shape: two
    ///     near-simultaneous edits to different categories on different PCs race — whichever apply
    ///     lands second overwrites every index with its own stale copy, silently discarding the other
    ///     side's just-made edit.
    ///     Granular shape (CS2M_TAXFIX=1, see <see cref="TaxFix"/>): <c>Indices</c>/<c>Rates</c> are a
    ///     parallel, same-length pair — only the categories that actually changed this tick, so
    ///     concurrent edits to different categories never clobber each other. A receiver branches on
    ///     whether <c>Indices</c> is populated, independent of its own gate, so it always understands
    ///     whichever shape the sender used.
    /// </summary>
    public class TaxSyncCommand : CommandBase
    {
        public int[] Rates { get; set; }

        /// <summary>Non-null/non-empty only in the granular (CS2M_TAXFIX) shape: index into the
        /// tax-rate array for each entry of <see cref="Rates"/>. Null =&gt; legacy full-array replace.</summary>
        public int[] Indices { get; set; }
    }
}
