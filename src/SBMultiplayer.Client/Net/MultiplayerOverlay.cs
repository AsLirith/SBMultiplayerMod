using BytingLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.UI;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Pseudos au-dessus des ghosts proches. Dessiné dans la scène du niveau (il faut sa caméra).
/// Le panneau du round et les notifications sont dans <see cref="GlobalOverlay"/>.
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
            ModLog.Error("Pseudos désactivés suite à une erreur", e);
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
        var (viewProjection, _) = GameAccess.GetCamera(scene);

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        try
        {
            foreach (RemotePlayer p in shownPlayers)
            {
                if (!p.TryGetPose(extrapolation, out Vector3 pos, out _))
                {
                    continue;
                }
                float distance = Vector3.Distance(pos, local);
                if (distance > cfg.NameTagMaxDistance)
                {
                    continue;
                }

                Vector4 clip = Vector4.Transform(new Vector4(pos + Vector3.Up * cfg.NameTagHeight, 1f), viewProjection);
                if (clip.W <= 0.01f)
                {
                    continue; // derrière la caméra
                }
                var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
                if (Math.Abs(ndc.X) > 1.2f || Math.Abs(ndc.Y) > 1.2f)
                {
                    continue;
                }
                var screen = new Vector2((ndc.X * 0.5f + 0.5f) * vp.Width, (0.5f - ndc.Y * 0.5f) * vp.Height);

                // Fondu sur le dernier quart de la distance, pour éviter l'apparition brutale.
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
