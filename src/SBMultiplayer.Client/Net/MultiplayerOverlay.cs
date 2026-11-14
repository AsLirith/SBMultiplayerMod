using BytingLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.UI;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Names above nearby ghosts. Drawn in the level scene (its camera is needed).
/// The round panel and notifications are in <see cref="GlobalOverlay"/>.
/// </summary>
internal sealed class MultiplayerOverlay
{
    private readonly TextDrawer text = new();
    private bool disabled;

    public void Disable(Exception e)
    {
        if (!disabled)
        {
            disabled = true;
            ModLog.Error("Name tags disabled after an error", e);
        }
    }

    public void Draw(SpriteBatch sb, Scene scene, Vector3? localPosition, IEnumerable<RemotePlayer> shownPlayers, float extrapolation)
    {
        ModConfig cfg = ModConfig.Current;
        if (disabled || localPosition is not Vector3 local || !cfg.ShowOtherPlayers || !cfg.ShowNameTags
            || cfg.NameTagMaxDistance <= 0 || !text.EnsureFont())
        {
            return;
        }

        Viewport vp = sb.GraphicsDevice.Viewport;
        float textScale = vp.Height / 1080f * cfg.NameTagScale;
        var (viewProjection, cameraPosition) = GameAccess.GetCamera(scene);
        // Spectating: our board stays on the start line, so distances are measured from the camera.
        Vector3 reference = Spectator.IsWatching ? cameraPosition : local;

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        try
        {
            foreach (RemotePlayer p in shownPlayers)
            {
                if (Spectator.IsWatching && p.Id == Spectator.TargetId)
                {
                    continue; // we're looking through their camera: their name would sit in the middle of the screen
                }
                if (!p.TryGetPose(extrapolation, out Vector3 pos, out _))
                {
                    continue;
                }
                float distance = Vector3.Distance(pos, reference);
                if (distance > cfg.NameTagMaxDistance)
                {
                    continue;
                }

                Vector4 clip = Vector4.Transform(new Vector4(pos + Vector3.Up * cfg.NameTagHeight, 1f), viewProjection);
                if (clip.W <= 0.01f)
                {
                    continue; // behind the camera
                }
                var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
                if (Math.Abs(ndc.X) > 1.2f || Math.Abs(ndc.Y) > 1.2f)
                {
                    continue;
                }
                var screen = new Vector2((ndc.X * 0.5f + 0.5f) * vp.Width, (0.5f - ndc.Y * 0.5f) * vp.Height);

                // Fade over the last quarter of the distance, to avoid popping in.
                float alpha = MathHelper.Clamp((cfg.NameTagMaxDistance - distance) / (cfg.NameTagMaxDistance * 0.25f), 0f, 1f);
                Vector2 size = text.Measure(p.Name, textScale);
                text.DrawOutlined(sb, p.Name, screen - new Vector2(size.X / 2f, size.Y), Color.White * alpha, textScale, alpha);
            }
        }
        finally
        {
            sb.End();
        }
    }
}
