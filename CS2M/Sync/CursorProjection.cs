extern alias ue;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2M.Sync
{
    /// <summary>
    ///     Projects a world position to normalized screen coordinates (0..1, origin
    ///     bottom-left) using the game's active camera. Kept in a plain (non-system)
    ///     class with the <c>ue</c> extern alias so it can name UnityEngine types
    ///     (Camera / Vector3) without colliding with the MessagePack.UnityShims
    ///     shim types. Returns false when there is no camera or the point is behind
    ///     the camera / far off screen.
    /// </summary>
    internal static class CursorProjection
    {
        private static Game.Rendering.CameraUpdateSystem _cameraSystem;

        public static bool TryProject(float3 world, out float nx, out float ny)
        {
            nx = 0f;
            ny = 0f;

            if (_cameraSystem == null)
            {
                World world0 = World.DefaultGameObjectInjectionWorld;
                if (world0 == null)
                {
                    return false;
                }

                _cameraSystem = world0.GetOrCreateSystemManaged<Game.Rendering.CameraUpdateSystem>();
            }

            // 'var' takes the type straight from Game.dll metadata (UnityEngine.CoreModule),
            // avoiding the ambiguity that naming UnityEngine.Camera in source would cause.
            var cam = _cameraSystem.activeCamera;
            if (cam == null)
            {
                cam = ue::UnityEngine.Camera.main;
            }

            if (cam == null)
            {
                return false;
            }

            var screen = cam.WorldToScreenPoint(new ue::UnityEngine.Vector3(world.x, world.y, world.z));
            if (screen.z <= 0f)
            {
                return false; // behind the camera
            }

            float w = cam.pixelWidth;
            float h = cam.pixelHeight;
            if (w <= 0f || h <= 0f)
            {
                return false;
            }

            nx = screen.x / w;
            ny = screen.y / h;

            // Small margin so labels near the screen edge still show.
            if (nx < -0.05f || nx > 1.05f || ny < -0.05f || ny > 1.05f)
            {
                return false;
            }

            return true;
        }
    }
}
