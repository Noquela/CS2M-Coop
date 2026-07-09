using Unity.Entities;

namespace CS2M.Sync
{
    /// <summary>
    ///     Tag on every NATIVE ghost entity (a <c>Game.Tools.Temp</c> node/edge/object) that
    ///     <see cref="PreviewApplySystem"/> injected on behalf of a REMOTE player's live build preview.
    ///     Carries the owning player's network id so the lifecycle systems can find and delete exactly
    ///     THIS player's ghosts (delete-then-regenerate each tick, TTL expiry, teardown) without ever
    ///     touching the local player's own tool preview or any other Temp entity. It is NEVER promoted to
    ///     a real build (no <c>CreationFlags.Permanent</c>, no apply path is ever run for these).
    /// </summary>
    public struct CS2M_RemotePreview : IComponentData
    {
        public int PlayerId;
    }
}
