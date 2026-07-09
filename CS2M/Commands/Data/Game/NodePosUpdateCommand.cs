using CS2M.API.Commands;

namespace CS2M.Commands.Data.Game
{
    /// <summary>
    ///     AtomicBatch follow-up (CS2M_ATOMIC=1): a CONTINUOUS stream of settled node positions for nodes
    ///     THIS side already shipped in a <see cref="NetBatchCommand"/>. A busy "star" junction re-seats its
    ///     nodes AFTER the batch was sent (NodeAlignSystem re-centres every time a new arm attaches, and
    ///     successive edge splits shift the node 1-2 m at a time), so the receiver — which applied the batch
    ///     with the OLD coordinate — is left GUESSING and drifts (POS-RELOC/FOLD-TRUST bending edges tens of
    ///     metres to the stale wire position). The builder (the only side that authored these nodes) streams
    ///     each node's newest settled position by IDENTITY; the receiver reconciles it via the same
    ///     MoveNodeWithCurves/detach-reattach machinery the batch apply already uses.
    ///
    ///     Wire format mirrors the other commands: flat parallel primitive arrays (MessagePack), indexed
    ///     positionally — <c>Ids[i]</c> pairs with <c>X[i]</c>/<c>Y[i]</c>/<c>Z[i]</c>. Batched ≤32 ids per
    ///     command by the sender.
    /// </summary>
    public class NodePosUpdateCommand : CommandBase
    {
        // Cross-PC node identity (CS2M_NodeSyncId) of each node whose settled position changed since it was
        // last sent by THIS side. Only ids this side ORIGINALLY authored/shipped appear here — the receiver
        // never re-broadcasts an update for a node it merely received (echo guard).
        public ulong[] Ids { get; set; }

        // The builder's newest Node.m_Position (settled, post-NodeAlign) for each id.
        public float[] X { get; set; }
        public float[] Y { get; set; }
        public float[] Z { get; set; }
    }
}
