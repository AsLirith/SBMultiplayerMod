using BytingLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SE;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>Provides a ghost's pose for the frame being drawn.</summary>
internal interface IGhostPoseSource
{
    /// <param name="extrapolation">Fraction [0, 1) elapsed since the last tick, given by the engine.</param>
    bool TryGetPose(float extrapolation, out Vector3 position, out Vector3 orientation);

    /// <summary>Board skin to show (game id), or null to keep the current texture.</summary>
    int? SkinId => null;
}

/// <summary>
/// Ghost board drawn like the game's ghosts (<c>SE.GhostBoard</c>), but fed
/// by any source (network buffer) instead of a replay file.
/// </summary>
internal sealed class NetGhostBoard : BoardVisual, IDrawWorldSkinRender, IBeforeDraw, IDisposable
{
    private readonly IGhostPoseSource source;
    private readonly TransparencyBatchSwapper transparencyBatch;
    private readonly ValueEvent<float> opacity;
    private readonly RasterizerState rasterizerState;

    /// <summary>Without any depth offset: a board behind a wall is never visible (Hide and Seek).</summary>
    private readonly RasterizerState exactRasterizerState = new() { CullMode = CullMode.CullClockwiseFace };
    private float extrapolation;

    public bool Visible { get; set; } = true;

    public NetGhostBoard(IGhostPoseSource source, BoardAssets assets, TransparencyBatchSwapper transparencyBatch,
        ValueEvent<float> opacity, int zFightingIndex, float depthBias = 0f)
        : base(assets, drawRealShadow: false, drawOutlines: false)
    {
        this.source = source;
        this.transparencyBatch = transparencyBatch;
        this.opacity = opacity;
        // Like SE.GhostBoard: a tiny depth offset per ghost avoids z-fighting between overlapping ghosts.
        // Keep the index small: the offset grows with it, and ghosts would show through thin walls.
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
            // Player's skin: the model's texture is swapped while drawing, like SE.GhostBoard.
            // The model is shared with our own board: the old texture is always put back.
            TextureGL? boardTex = Assets.Model.Value.Textures?.Get(0);
            Ref<Texture2D>? skin = source.SkinId is int skinId ? SkinCache.Get(skinId) : null;
            Ref<Texture2D>? previous = boardTex?.Image.Tex2D;
            if (boardTex != null && skin != null)
            {
                boardTex.Image.Tex2D = skin;
            }
            try
            {
                // Unlike SE.GhostBoard, without the board's own depth bias (-0.01, applyDepthBias): it pulls the board
                // so far towards the camera that it shows through walls and decor.
                using (shader.UseRasterizer(Net.HideSeek.IsActive ? exactRasterizerState : rasterizerState))
                using (shaderRender?.Color.Use(new Vector4(1f, 1f, 1f, opacity.Value)))
                {
                    DrawInternalBase(shader, shaderSkin, shaderRender, transform, null, null, applyDepthBias: false);
                }
            }
            finally
            {
                if (boardTex != null && skin != null)
                {
                    boardTex.Image.Tex2D = previous!;
                }
            }
        }

        if (shaderRender != null && opacity.Value < 1f)
        {
            // Transparent objects: drawn later, sorted by depth (like SE.GhostBoard).
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
        exactRasterizerState.Dispose();
    }
}
