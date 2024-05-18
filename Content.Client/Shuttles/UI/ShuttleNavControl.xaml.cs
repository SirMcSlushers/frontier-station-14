using System.Numerics;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using JetBrains.Annotations;
using Robust.Client.AutoGenerated;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Collections;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;

namespace Content.Client.Shuttles.UI;

[GenerateTypedNameReferences]
public sealed partial class ShuttleNavControl : BaseShuttleControl
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IUserInterfaceManager _uiManager = default!;
    private readonly SharedShuttleSystem _shuttles;
    private readonly SharedTransformSystem _transform;

    /// <summary>
    /// Used to transform all of the radar objects. Typically is a shuttle console parented to a grid.
    /// </summary>
    private EntityCoordinates? _coordinates;

    private Angle? _rotation;

    private Dictionary<NetEntity, List<DockingPortState>> _docks = new();

    public bool ShowIFF { get; set; } = true;
    public bool ShowIFFShuttles { get; set; } = true;
    public bool ShowDocks { get; set; } = true;

    /// <summary>
    ///   If present, called for every IFF. Must determine if it should or should not be shown.
    /// </summary>
    public Func<EntityUid, MapGridComponent, IFFComponent?, bool>? IFFFilter { get; set; } = null;

    /// <summary>
    /// Raised if the user left-clicks on the radar control with the relevant entitycoordinates.
    /// </summary>
    public Action<EntityCoordinates>? OnRadarClick;

    private List<Entity<MapGridComponent>> _grids = new();

    public ShuttleNavControl() : base(64f, 256f, 256f)
    {
        RobustXamlLoader.Load(this);
        _shuttles = EntManager.System<SharedShuttleSystem>();
        _transform = EntManager.System<SharedTransformSystem>();
    }

    public void SetMatrix(EntityCoordinates? coordinates, Angle? angle)
    {
        _coordinates = coordinates;
        _rotation = angle;
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);

        if (_coordinates == null || _rotation == null || args.Function != EngineKeyFunctions.UIClick ||
            OnRadarClick == null)
        {
            return;
        }

        var a = InverseScalePosition(args.RelativePosition);
        var relativeWorldPos = a with { Y = -a.Y };
        relativeWorldPos = _rotation.Value.RotateVec(relativeWorldPos);
        var coords = _coordinates.Value.Offset(relativeWorldPos);
        OnRadarClick?.Invoke(coords);
    }

    /// <summary>
    /// Gets the entity coordinates of where the mouse position is, relative to the control.
    /// </summary>
    [PublicAPI]
    public EntityCoordinates GetMouseCoordinatesFromCenter()
    {
        if (_coordinates == null || _rotation == null)
        {
            return EntityCoordinates.Invalid;
        }

        var pos = _uiManager.MousePositionScaled.Position - GlobalPosition;
        var relativeWorldPos = _rotation.Value.RotateVec(pos);

        // I am not sure why the resulting point is 20 units under the mouse.
        return _coordinates.Value.Offset(relativeWorldPos);
    }

    public void UpdateState(NavInterfaceState state)
    {
        SetMatrix(EntManager.GetCoordinates(state.Coordinates), state.Angle);

        WorldMaxRange = state.MaxRange;

        if (WorldMaxRange < WorldRange)
        {
            ActualRadarRange = WorldMaxRange;
        }

        if (WorldMaxRange < WorldMinRange)
            WorldMinRange = WorldMaxRange;

        ActualRadarRange = Math.Clamp(ActualRadarRange, WorldMinRange, WorldMaxRange);

        _docks = state.Docks;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        DrawBacking(handle);
        DrawCircles(handle);

        // No data
        if (_coordinates == null || _rotation == null)
        {
            return;
        }

        var xformQuery = EntManager.GetEntityQuery<TransformComponent>();
        var fixturesQuery = EntManager.GetEntityQuery<FixturesComponent>();
        var bodyQuery = EntManager.GetEntityQuery<PhysicsComponent>();

        if (!xformQuery.TryGetComponent(_coordinates.Value.EntityId, out var xform)
            || xform.MapID == MapId.Nullspace)
        {
            return;
        }

        var mapPos = _transform.ToMapCoordinates(_coordinates.Value);
        var offset = _coordinates.Value.Position;
        var posMatrix = Matrix3.CreateTransform(offset, _rotation.Value);
        var (_, ourEntRot, ourEntMatrix) = _transform.GetWorldPositionRotationMatrix(_coordinates.Value.EntityId);
        Matrix3.Multiply(posMatrix, ourEntMatrix, out var ourWorldMatrix);
        var ourWorldMatrixInvert = ourWorldMatrix.Invert();

        // Draw our grid in detail
        var ourGridId = xform.GridUid;
        if (EntManager.TryGetComponent<MapGridComponent>(ourGridId, out var ourGrid) &&
            fixturesQuery.HasComponent(ourGridId.Value))
        {
            var ourGridMatrix = _transform.GetWorldMatrix(ourGridId.Value);
            Matrix3.Multiply(in ourGridMatrix, in ourWorldMatrixInvert, out var matrix);
            var color = _shuttles.GetIFFColor(ourGridId.Value, self: true);

            DrawGrid(handle, matrix, (ourGridId.Value, ourGrid), color);
            DrawDocks(handle, ourGridId.Value, matrix);
        }

        var invertedPosition = _coordinates.Value.Position - offset;
        invertedPosition.Y = -invertedPosition.Y;
        // Don't need to transform the InvWorldMatrix again as it's already offset to its position.

        // Draw radar position on the station
        var radarPos = invertedPosition;
        const float radarVertRadius = 2f;

        var radarPosVerts = new Vector2[]
        {
            ScalePosition(radarPos + new Vector2(0f, -radarVertRadius)),
            ScalePosition(radarPos + new Vector2(radarVertRadius / 2f, 0f)),
            ScalePosition(radarPos + new Vector2(0f, radarVertRadius)),
            ScalePosition(radarPos + new Vector2(radarVertRadius / -2f, 0f)),
        };

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, radarPosVerts, Color.Lime);

        var rot = ourEntRot + _rotation.Value;
        var viewBounds = new Box2Rotated(new Box2(-WorldRange, -WorldRange, WorldRange, WorldRange).Translated(mapPos.Position), rot, mapPos.Position);
        var viewAABB = viewBounds.CalcBoundingBox();

        _grids.Clear();
        _mapManager.FindGridsIntersecting(xform.MapID, new Box2(mapPos.Position - MaxRadarRangeVector, mapPos.Position + MaxRadarRangeVector), ref _grids, approx: true, includeMap: false);

        // Frontier - collect blip location data outside foreach - more changes ahead
        var blipDataList = new List<BlipData>();

        // Draw other grids... differently
        foreach (var grid in _grids)
        {
            var gUid = grid.Owner;
            if (gUid == ourGridId || !fixturesQuery.HasComponent(gUid))
                continue;

            var gridBody = bodyQuery.GetComponent(gUid);
            EntManager.TryGetComponent<IFFComponent>(gUid, out var iff);

            if (!_shuttles.CanDraw(gUid, gridBody, iff))
                continue;

            var gridMatrix = _transform.GetWorldMatrix(gUid);
            Matrix3.Multiply(in gridMatrix, in ourWorldMatrixInvert, out var matty);
            var color = _shuttles.GetIFFColor(grid, self: false, iff);

            // Others default:
            // Color.FromHex("#FFC000FF")
            // Hostile default: Color.Firebrick
            var labelName = _shuttles.GetIFFLabel(grid, self: false, iff);

            var isPlayerShuttle = iff != null && (iff.Flags & IFFFlags.IsPlayerShuttle) != 0x0;
            var shouldDrawIFF = ShowIFF && labelName != null && (iff != null && (iff.Flags & IFFFlags.HideLabel) == 0x0);
            if (IFFFilter != null)
            {
                shouldDrawIFF &= IFFFilter(gUid, grid.Comp, iff);
            }
            if (isPlayerShuttle)
            {
                shouldDrawIFF &= ShowIFFShuttles;
            }

            if (shouldDrawIFF)
            {
                var gridCentre = matty.Transform(gridBody.LocalCenter);
                gridCentre.Y = -gridCentre.Y;

                // The actual position in the UI. We offset the matrix position to render it off by half its width
                // plus by the offset.
                var uiPosition = ScalePosition(gridCentre) / UIScale;

                // Confines the UI position within the viewport.
                var uiXCentre = (int) Width / 2;
                var uiYCentre = (int) Height / 2;
                var uiXOffset = uiPosition.X - uiXCentre;
                var uiYOffset = uiPosition.Y - uiYCentre;
                var uiDistance = (int) Math.Sqrt(Math.Pow(uiXOffset, 2) + Math.Pow(uiYOffset, 2));
                var uiX = uiXCentre * uiXOffset / uiDistance;
                var uiY = uiYCentre * uiYOffset / uiDistance;

                var isOutsideRadarCircle = uiDistance > Math.Abs(uiX) && uiDistance > Math.Abs(uiY);
                if (isOutsideRadarCircle)
                {
                    // 0.95f for offsetting the icons slightly away from edge of radar so it doesnt clip.
                    uiX = uiXCentre * uiXOffset / uiDistance * 0.95f;
                    uiY = uiYCentre * uiYOffset / uiDistance * 0.95f;
                    uiPosition = new Vector2(
                        x: uiX + uiXCentre,
                        y: uiY + uiYCentre
                    );
                }

                var scaledMousePosition = GetMouseCoordinatesFromCenter().Position * UIScale;
                var isMouseOver = Vector2.Distance(scaledMousePosition, uiPosition * UIScale) < 30f;

                // Distant stations that are not player controlled ships
                var isDistantPOI = iff != null || (iff == null || (iff.Flags & IFFFlags.IsPlayerShuttle) == 0x0);

                var distance = gridCentre.Length();

                // Shows decimal when distance is < 50m, otherwise pointless to show it.
                var displayedDistance = distance < 50f ? $"{distance:0.0}" : distance < 1000 ? $"{distance:0}" : $"{distance / 1000:0.0}k";
                var labelText = Loc.GetString("shuttle-console-iff-label", ("name", labelName)!, ("distance", displayedDistance));

                if (!isOutsideRadarCircle || isDistantPOI || isMouseOver)
                {
                    // Calculate unscaled offsets.
                    var labelDimensions = handle.GetDimensions(Font, labelText, 1f);
                    var blipSize = RadarBlipSize * 0.7f;
                    var labelOffset = new Vector2()
                    {
                        X = uiPosition.X > Width / 2f
                            ? -labelDimensions.X - blipSize // right align the text to left of the blip
                            : blipSize, // left align the text to the right of the blip
                        Y = -labelDimensions.Y / 2f
                    };

                    handle.DrawString(Font, (uiPosition + labelOffset) * UIScale, labelText, UIScale, color);
                }

                blipDataList.Add(new BlipData
                {
                    IsOutsideRadarCircle = isOutsideRadarCircle,
                    UiPosition = uiPosition,
                    VectorToPosition = uiPosition - new Vector2(uiXCentre, uiYCentre),
                    Color = color
                });
            }

            // Don't skip drawing blips if they're out of range.
            DrawBlips(handle, blipDataList);

            // Detailed view
            var gridAABB = gridMatrix.TransformBox(grid.Comp.LocalAABB);

            // Skip drawing if it's out of range.
            if (!gridAABB.Intersects(viewAABB))
                continue;

            DrawGrid(handle, matty, grid, color);
            DrawDocks(handle, gUid, matty);
        }
    }

    private void DrawDocks(DrawingHandleScreen handle, EntityUid uid, Matrix3 matrix)
    {
        if (!ShowDocks)
            return;

        const float DockScale = 0.6f;
        var nent = EntManager.GetNetEntity(uid);

        if (_docks.TryGetValue(nent, out var docks))
        {
            foreach (var state in docks)
            {
                var position = state.Coordinates.Position;
                var uiPosition = matrix.Transform(position);

                if (uiPosition.Length() > (WorldRange * 2f) - DockScale)
                    continue;

                var color = Color.ToSrgb(Color.Magenta);

                var verts = new[]
                {
                    matrix.Transform(position + new Vector2(-DockScale, -DockScale)),
                    matrix.Transform(position + new Vector2(DockScale, -DockScale)),
                    matrix.Transform(position + new Vector2(DockScale, DockScale)),
                    matrix.Transform(position + new Vector2(-DockScale, DockScale)),
                };

                for (var i = 0; i < verts.Length; i++)
                {
                    var vert = verts[i];
                    vert.Y = -vert.Y;
                    verts[i] = ScalePosition(vert);
                }

                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, verts, color.WithAlpha(0.8f));
                handle.DrawPrimitives(DrawPrimitiveTopology.LineStrip, verts, color);
            }
        }
    }

    private Vector2 InverseScalePosition(Vector2 value)
    {
        return (value - MidPointVector) / MinimapScale;
    }

    public class BlipData
    {
        public bool IsOutsideRadarCircle { get; set; }
        public Vector2 UiPosition { get; set; }
        public Vector2 VectorToPosition { get; set; }
        public Color Color { get; set; }
    }

    private const int RadarBlipSize = 15;
    private const int RadarFontSize = 10;

    /**
     * Frontier - Adds blip style triangles that are on ships or pointing towards ships on the edges of the radar.
     * Draws blips at the BlipData's uiPosition and uses VectorToPosition to rotate to point towards ships.
     */
    private void DrawBlips(
        DrawingHandleBase handle,
        List<BlipData> blipDataList
    )
    {
        var blipValueList = new Dictionary<Color, ValueList<Vector2>>();

        foreach (var blipData in blipDataList)
        {
            var triangleShapeVectorPoints = new[]
            {
                new Vector2(0, 0),
                new Vector2(RadarBlipSize, 0),
                new Vector2(RadarBlipSize * 0.5f, RadarBlipSize)
            };

            if (blipData.IsOutsideRadarCircle)
            {
                // Calculate the angle of rotation
                var angle = (float) Math.Atan2(blipData.VectorToPosition.Y, blipData.VectorToPosition.X) + -1.6f;

                // Manually create a rotation matrix
                var cos = (float) Math.Cos(angle);
                var sin = (float) Math.Sin(angle);
                float[,] rotationMatrix = { { cos, -sin }, { sin, cos } };

                // Rotate each vertex
                for (var i = 0; i < triangleShapeVectorPoints.Length; i++)
                {
                    var vertex = triangleShapeVectorPoints[i];
                    var x = vertex.X * rotationMatrix[0, 0] + vertex.Y * rotationMatrix[0, 1];
                    var y = vertex.X * rotationMatrix[1, 0] + vertex.Y * rotationMatrix[1, 1];
                    triangleShapeVectorPoints[i] = new Vector2(x, y);
                }
            }

            var triangleCenterVector =
                (triangleShapeVectorPoints[0] + triangleShapeVectorPoints[1] + triangleShapeVectorPoints[2]) / 3;

            // Calculate the vectors from the center to each vertex
            var vectorsFromCenter = new Vector2[3];
            for (int i = 0; i < 3; i++)
            {
                vectorsFromCenter[i] = (triangleShapeVectorPoints[i] - triangleCenterVector) * UIScale;
            }

            // Calculate the vertices of the new triangle
            var newVerts = new Vector2[3];
            for (var i = 0; i < 3; i++)
            {
                newVerts[i] = (blipData.UiPosition * UIScale) + vectorsFromCenter[i];
            }

            if (!blipValueList.TryGetValue(blipData.Color, out var valueList))
            {
                valueList = new ValueList<Vector2>();

            }
            valueList.Add(newVerts[0]);
            valueList.Add(newVerts[1]);
            valueList.Add(newVerts[2]);
            blipValueList[blipData.Color] = valueList;
        }

        // One draw call for every color we have
        foreach (var color in blipValueList)
        {
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, color.Value.Span, color.Key);
        }
    }
}
