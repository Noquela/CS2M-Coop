using Colossal.Mathematics;
using CS2M.API.Commands;
using CS2M.Commands.Data.Game;
using CS2M.Sync;
using Unity.Mathematics;

namespace CS2M.Commands.Handler.Game
{
    /// <summary>v55: stores a remote player's live tool-preview curve for ToolPreviewSystem to draw.</summary>
    public class ToolPreviewHandler : CommandHandler<ToolPreviewCommand>
    {
        public ToolPreviewHandler()
        {
            TransactionCmd = false;
            RelayOnServer = true; // every client should see every OTHER client's preview
        }

        protected override void Handle(ToolPreviewCommand c)
        {
            var bez = new Bezier4x3(
                new float3(c.Ax, c.Ay, c.Az),
                new float3(c.Bx, c.By, c.Bz),
                new float3(c.Cx, c.Cy, c.Cz),
                new float3(c.Dx, c.Dy, c.Dz));
            RemoteToolPreviews.Update(c.SenderId, c.Active, bez, c.Username);
        }
    }
}
