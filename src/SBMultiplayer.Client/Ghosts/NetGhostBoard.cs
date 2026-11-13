using BytingLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SE;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>Fournit la pose d'un ghost pour la frame en cours de dessin.</summary>
internal interface IGhostPoseSource
{
    /// <param name="extrapolation">Fraction [0, 1) écoulée depuis le dernier tick, donnée par le moteur.</param>
    bool TryGetPose(float extrapolation, out Vector3 position, out Vector3 orientation);
}

/// <summary>
/// Planche fantôme dessinée comme les ghosts du jeu (<c>SE.GhostBoard</c>), mais alimentée
/// par une source quelconque (tampon réseau) au lieu d'un fichier de replay.
/// </summary>
internal sealed class NetGhostBoard : BoardVisual, IDrawWorldSkinRender, IBeforeDraw, IDisposable
{
    private readonly IGhostPoseSource source;
    private readonly TransparencyBatchSwapper transparencyBatch;
    private readonly ValueEvent<float> opacity;
    private readonly RasterizerState rasterizerState;
    private float extrapolation;

    public bool Visible { get; set; } = true;

    public NetGhostBoard(IGhostPoseSource source, BoardAssets assets, TransparencyBatchSwapper transparencyBatch,
        ValueEvent<float> opacity, int zFightingIndex, float depthBias = 0f)
        : base(assets, drawRealShadow: false, drawOutlines: false)
    {
        this.source = source;
        this.transparencyBatch = transparencyBatch;
        this.opacity = opacity;
        // Même réglage que SE.GhostBoard : évite le z-fighting entre ghosts superposés.
        rasterizerState = new RasterizerState
        {
            DepthBias = -zFightingIndex * 1E-05f + depthBias,
            CullMode = CullMode.CullClockwiseFace,
        };
    }

    public void BeforeDraw(Scene? popup, float extrapolation)
    {
        this.extrapolation = extrapolation;
    }

    public void Draw(IShaderWorld shader, IShaderSkin shaderSkin, ShaderRender? shaderRender, bool baked)
    {
        if (!Visible || baked || opacity.Value <= 0f || (shaderRender != null && shaderRender.InverseHullOutlines.GetValue()))
        {
            return;
        }
        if (!source.TryGetPose(extrapolation, out Vector3 pos, out Vector3 rot))
        {
            return;
        }

        Matrix transform = Matrix.CreateFromYawPitchRoll(rot.Y, rot.X, rot.Z) * Matrix.CreateTranslation(pos);
        void DrawNow()
        {
            using (shader.UseRasterizer(rasterizerState))
            using (shaderRender?.Color.Use(new Vector4(1f, 1f, 1f, opacity.Value)))
            {
                DrawInternalBase(shader, shaderSkin, shaderRender, transform, null, null, applyDepthBias: true);
            }
        }

        if (shaderRender != null && opacity.Value < 1f)
        {
            // Objets transparents : dessinés plus tard, triés par profondeur (comme SE.GhostBoard).
            transparencyBatch.DrawLater(new Vector3(0f, -pos.Y, 0f), baked, DrawNow);
        }
        else
        {
            DrawNow();
        }
    }

    public void Dispose()
    {
        rasterizerState.Dispose();
    }
}
