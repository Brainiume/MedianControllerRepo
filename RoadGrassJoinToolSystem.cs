using System;
using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace RoadGrass
{
    /// <summary>
    /// Explicit two-click median-section selector. Vanilla raycasting chooses
    /// the road; median footprints are hit-tested from composition offsets and
    /// EdgeGeometry so split and RoadBuilder medians remain independently selectable.
    /// </summary>
    [Preserve]
    internal partial class RoadGrassJoinToolSystem : ToolBaseSystem
    {
        private const float PickLength = 12f;
        // Traffic's lane-connection selector raycasts the camera ray against
        // the actual lane-handle curve and keeps a minimum world-space radius
        // for narrow geometry. Median selection follows the same model: the
        // native median half-width is added to this cursor radius.
        private const float MinimumCursorRadius = 1.5f;
        private const float CursorRadiusFovScale = 0.01f;
        private const float JunctionHoverRadius = 40f;
        private const float OutlineWidth = 0.35f;

        private static readonly Color HoverColor = new Color(0.05f, 0.85f, 1f, 1f);
        private static readonly Color SelectedColor = new Color(1f, 0.55f, 0.05f, 1f);
        private static readonly Color ValidColor = new Color(0.15f, 1f, 0.25f, 1f);
        private static readonly Color InvalidColor = new Color(1f, 0.1f, 0.1f, 1f);
        private static readonly Color LinkedColor = new Color(0.75f, 0.2f, 1f, 1f);
        private static readonly Color CandidateColor = new Color(0.05f, 0.85f, 1f, 0.35f);

        private NetPrefab m_ToolPrefab;
        private RoadGrassMedianAnchorKey? m_FirstAnchor;
        internal bool HasPendingSelection => m_FirstAnchor.HasValue;
        private OverlayRenderSystem m_OverlayRenderSystem;
        private Camera m_MainCamera;
        private RoadGrassUISystem Options => World.GetExistingSystemManaged<RoadGrassUISystem>();

        internal void ClearTransientSelection()
        {
            m_PolygonPoints.Clear();
            m_PolygonCornerStarts.Clear(); m_PolygonCornerSnaps.Clear();
            RoadGrassJoinCompositionSystem.Instance?.SelectManualMedian(0);
            m_FirstAnchor = null;
            RoadGrassJoinCompositionSystem.Instance?.ClearStylePreviewCache();
            TooltipState = RoadGrassJoinTooltipState.SelectFirst;
        }

        private enum OverlayCommandType : byte
        {
            Curve,
            Line
        }

        private struct OverlayCommand
        {
            internal OverlayCommandType Type;
            internal Color Color;
            internal Bezier4x3 Curve;
            internal Line3.Segment Line;
            internal float Width;
        }

        private struct DrawOverlayJob : IJob
        {
            [ReadOnly]
            internal NativeArray<OverlayCommand> Commands;
            internal OverlayRenderSystem.Buffer Buffer;

            public void Execute()
            {
                for (int i = 0; i < Commands.Length; i++)
                {
                    OverlayCommand command = Commands[i];
                    if (command.Type == OverlayCommandType.Curve)
                    {
                        Buffer.DrawCurve(command.Color, command.Curve, command.Width);
                    }
                    else
                    {
                        Buffer.DrawLine(command.Color, command.Line, command.Width);
                    }
                }
            }
        }

        internal RoadGrassJoinTooltipState TooltipState { get; private set; } =
            RoadGrassJoinTooltipState.SelectFirst;

        public override string toolID => "RoadGrassJoinMedian";
        internal Game.Input.IProxyAction ApplyInput => applyAction;
        internal Game.Input.IProxyAction SecondaryApplyInput => secondaryApplyAction;

        public override PrefabBase GetPrefab() => m_ToolPrefab;

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            if (prefab is NetPrefab netPrefab &&
                netPrefab.name == RoadGrassPrefabSystem.PrefabName)
            {
                m_ToolPrefab = netPrefab;
                return true;
            }
            return false;
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            Enabled = false;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.Road;
            m_ToolRaycastSystem.collisionMask =
                CollisionMask.OnGround | CollisionMask.Overground | CollisionMask.Underground;
        }

        [Preserve]
        protected override void OnStartRunning()
        {
            // Install the override before native action setup touches Cancel.
            cancelActionOverride = Mod.CloseToolAction;
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            secondaryApplyAction.shouldBeEnabled = true;
            // Follow native keyboard/gamepad Cancel bindings without competing with mouse removal.
            cancelAction.shouldBeEnabled = true;
            m_FirstAnchor = null;
            m_MainCamera = Camera.main;
        }

        [Preserve]
        protected override void OnStopRunning()
        {
            // Native ResetActions disables the current actions before clearing overrides.
            base.OnStopRunning();
            ClearTransientSelection();
            m_MainCamera = null;
        }

        [Preserve]
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (m_ToolSystem.activeTool != this) return inputDeps;
            if (cancelAction.WasPressedThisFrame())
            {
                m_ToolSystem.activeTool = m_DefaultToolSystem;
                return inputDeps;
            }
            RoadGrassJoinCompositionSystem manager = RoadGrassJoinCompositionSystem.Instance;
            RoadGrassResolvedMedianAnchor hovered = null;
            bool hitRoad = false;
            Entity overlayNode = Entity.Null;
            RoadGrassResolvedMedianAnchor selectedFirst = null;

            bool hasRoadRaycast =
                GetRaycastResult(out Entity hitEntity, out RaycastHit hit);
            hitRoad = hasRoadRaycast;
            bool hasCursorRay = TryGetCursorRay(
                out Line3.Segment cursorRay,
                out float fovTan);

            // Custom meshes are not vanilla road-raycast targets. Pick them before endpoint
            // resolution, and use the same interaction in every mode that manages them.
            bool managesManual = Options.ActiveMode == MedianControllerMode.Edit || Options.ActiveMode == MedianControllerMode.Reset ||
                (Options.ActiveMode == MedianControllerMode.Polygon && !HasPolygonCorners);
            if (manager != null && managesManual && hasCursorRay)
            {
                int manual = PickManualMedian(manager, cursorRay, fovTan);
                if (manual != 0)
                {
                    bool reset = Options.ActiveMode == MedianControllerMode.Reset;
                    TooltipState = reset ? RoadGrassJoinTooltipState.ResetManualMedian : RoadGrassJoinTooltipState.SelectManualMedian;
                    if (secondaryApplyAction.WasReleasedThisFrame() || (reset && applyAction.WasReleasedThisFrame()))
                    {
                        ClearTransientSelection();
                        manager.RemoveManualMedian(manual);
                    }
                    else if (applyAction.WasReleasedThisFrame())
                    {
                        ClearTransientSelection();
                        manager.SelectManualMedian(manual);
                    }
                    return DrawManualMedianSelection(inputDeps, manager, manual);
                }
            }

            if (m_FirstAnchor.HasValue && manager != null)
                manager.TryResolveAnchor(m_FirstAnchor.Value, out selectedFirst);

            if (manager != null && selectedFirst != null && hasCursorRay)
            {
                // Once the junction is known, its median curves are
                // authoritative. This intentionally works even while the
                // vanilla net raycast is over the junction gap or another
                // road surface.
                TryPickAnchor(
                    manager.GetNodeAnchors(selectedFirst.Node),
                    cursorRay,
                    fovTan,
                    out hovered);
                overlayNode = selectedFirst.Node;
            }
            else if (manager != null && hasRoadRaycast && hasCursorRay)
            {
                if (TryResolveEdge(hitEntity, out Entity edge))
                {
                    if (EntityManager.TryGetComponent(edge, out Edge edgeData))
                    {
                        List<RoadGrassResolvedMedianAnchor> candidates;
                        if (TryGetHoveredJunction(
                                edgeData,
                                hit.m_Position,
                                out overlayNode))
                        {
                            candidates = manager.GetNodeAnchors(overlayNode);
                        }
                        else
                        {
                            candidates = manager.GetNodeAnchors(edgeData.m_Start);
                            if (edgeData.m_End != edgeData.m_Start)
                            {
                                candidates.AddRange(
                                    manager.GetNodeAnchors(edgeData.m_End));
                            }
                        }
                        TryPickAnchor(
                            candidates,
                            cursorRay,
                            fovTan,
                            out hovered);
                    }
                    else
                    {
                        TryPickAnchor(
                            manager,
                            edge,
                            cursorRay,
                            fovTan,
                            out hovered);
                    }
                }
                else if (TryResolveNode(hitEntity, out Entity node))
                {
                    overlayNode = node;
                    TryPickAnchor(
                        manager.GetNodeAnchors(node),
                        cursorRay,
                        fovTan,
                        out hovered);
                }
            }

            if (m_FirstAnchor.HasValue &&
                (manager == null || !manager.TryResolveAnchor(
                    m_FirstAnchor.Value,
                    out _)))
            {
                m_FirstAnchor = null;
            }

            // The existing endpoint footprint keeps cap editing local to the
            // junction. Away from it, Edit/Reset Surface can pick a full strip.
            RoadGrassResolvedMedianAnchor segment = null;
            if (manager != null && Options.ActiveMode == MedianControllerMode.Polygon)
                return UpdatePolygon(inputDeps, manager, hovered, overlayNode, hasCursorRay, cursorRay, fovTan);
            if (manager != null && (Options.ActiveMode == MedianControllerMode.Edit || Options.ActiveMode == MedianControllerMode.Reset))
            {
                if (hasCursorRay && applyAction.WasReleasedThisFrame()) manager.SelectManualMedian(0);
                if (manager.SelectedManualMedianId != 0)
                    inputDeps = DrawManualMedianSelection(inputDeps, manager, 0);
            }
            if (manager != null && Options.ActiveMode == MedianControllerMode.Edit && Options.ExtendIntoIntersection)
                return UpdateManualTaper(inputDeps,manager,hovered,overlayNode,hasCursorRay,cursorRay,fovTan);
            bool surfaceMode = (Options.ActiveMode == MedianControllerMode.Edit && !Options.ExtendIntoIntersection) ||
                (Options.ActiveMode == MedianControllerMode.Reset &&
                 (Options.ResetFilters & MedianResetFilters.Surface) != 0);
            if (surfaceMode && manager != null && hasRoadRaycast && hasCursorRay &&
                TryResolveEdge(hitEntity, out Entity segmentEdge))
            {
                TryPickSegment(manager.GetSegmentAnchors(segmentEdge), cursorRay, fovTan, out segment);
            }
            var endpointSegment = surfaceMode ? manager?.GetSegmentForEndpoint(hovered) : null;
            if (segment != null && (endpointSegment == null || !endpointSegment.Key.Equals(segment.Key)))
            {
                bool canApply = Options.ActiveMode == MedianControllerMode.Edit
                    ? manager.CanEditSegmentSurface(segment, Options.EditSurfaceChoice)
                    : manager.CanResetSegmentSurface(segment);
                if (applyAction.WasReleasedThisFrame() && !secondaryApplyAction.WasReleasedThisFrame() && canApply)
                {
                    if (Options.ActiveMode == MedianControllerMode.Edit)
                        manager.TryEditSegmentSurface(segment, Options.EditSurfaceChoice);
                    else manager.TryResetSegmentSurface(segment);
                }
                TooltipState = !canApply ? RoadGrassJoinTooltipState.UnsupportedStyle :
                    Options.ActiveMode == MedianControllerMode.Edit ? RoadGrassJoinTooltipState.EditSegment :
                    RoadGrassJoinTooltipState.ResetSegment;
                return DrawSegmentOverlay(inputDeps, segment, canApply ? ValidColor : InvalidColor);
            }

            RoadGrassResolvedMedianAnchor automaticTarget = null;
            if (Options.ActiveMode == MedianControllerMode.Connect &&
                !m_FirstAnchor.HasValue && hovered != null && manager != null)
            {
                TryGetUniqueAutomaticTarget(
                    manager,
                    hovered,
                    out automaticTarget);
            }

            // Handle each right-click once, on release: cancel a staged
            // selection first, otherwise remove the hovered complete pair.
            // Tool exit belongs to the game's UI back/escape handling.
            if (secondaryApplyAction.WasReleasedThisFrame())
            {
                if (m_FirstAnchor.HasValue)
                {
                    m_FirstAnchor = null;
                }
                else if (hovered != null && Options.ActiveMode == MedianControllerMode.Connect)
                {
                    manager?.RemoveAnchorLinks(hovered.Key);
                }
            }
            else if (applyAction.WasReleasedThisFrame() && hovered != null && manager != null)
            {
                if (Options.ActiveMode == MedianControllerMode.Edit)
                {
                    manager.TryEditEndpoint(hovered, Options.EditCapChoice, Options.EditSurfaceChoice, Options.ExtendIntoIntersection);
                }
                else if (Options.ActiveMode == MedianControllerMode.Reset)
                {
                    manager.TryResetEndpoint(hovered, Options.ResetFilters);
                }
                else if (!m_FirstAnchor.HasValue)
                {
                    if (automaticTarget != null &&
                        manager.AddLink(hovered, automaticTarget))
                    {
                        m_FirstAnchor = null;
                        automaticTarget = null;
                    }
                    else
                    {
                        m_FirstAnchor = hovered.Key;
                    }
                }
                else if (m_FirstAnchor.Value.Equals(hovered.Key))
                {
                    m_FirstAnchor = null;
                }
                else if (manager.TryResolveAnchor(
                             m_FirstAnchor.Value,
                             out RoadGrassResolvedMedianAnchor first) &&
                         manager.AddLink(first, hovered))
                {
                    m_FirstAnchor = null;
                }
            }

            bool junctionHasCandidates = manager != null &&
                overlayNode != Entity.Null &&
                manager.GetNodeAnchors(overlayNode).Count > 0;
            UpdateTooltipState(
                manager,
                hovered,
                hitRoad,
                junctionHasCandidates,
                automaticTarget);
            inputDeps = DrawOverlays(
                inputDeps,
                manager,
                hovered,
                overlayNode,
                automaticTarget);
            if (surfaceMode && hovered != null && manager != null)
            {
                var strip = manager.GetSegmentForEndpoint(hovered);
                if (strip != null) inputDeps = DrawSegmentOverlay(inputDeps, strip,
                    CanApplyEndpoint(manager, hovered) ? ValidColor : InvalidColor);
            }
            return inputDeps;
        }

        private bool TryPickSegment(List<RoadGrassResolvedMedianAnchor> candidates, Line3.Segment ray,
            float fovTan, out RoadGrassResolvedMedianAnchor best)
        {
            best = null;
            float bestScore = float.MaxValue;
            foreach (var candidate in candidates)
            {
                if (!RoadGrassMedianGeometry.TryGetSegmentStrip(EntityManager, candidate, out var strip)) continue;
                bool start = TryScoreFootprint(strip.m_Start.m_Left, strip.m_Start.m_Right, ray, fovTan, out float a);
                bool end = TryScoreFootprint(strip.m_End.m_Left, strip.m_End.m_Right, ray, fovTan, out float b);
                float score = math.min(start ? a : float.MaxValue, end ? b : float.MaxValue);
                if (score >= bestScore) continue;
                bestScore = score;
                best = candidate;
            }
            return best != null;
        }

        private JobHandle UpdateManualTaper(JobHandle inputDeps, RoadGrassJoinCompositionSystem manager,
            RoadGrassResolvedMedianAnchor hovered, Entity node, bool hasRay, Line3.Segment ray, float fovTan)
        {
            if (secondaryApplyAction.WasReleasedThisFrame())
            {
                if (!m_FirstAnchor.HasValue) manager.TryRemoveExtension(hovered);
                m_FirstAnchor = null;
                TooltipState = RoadGrassJoinTooltipState.SelectTaperStart;
                return inputDeps;
            }
            if (!m_FirstAnchor.HasValue)
            {
                bool available = hovered != null && CanApplyEndpoint(manager,hovered);
                TooltipState = available ? RoadGrassJoinTooltipState.PickTaperStart : RoadGrassJoinTooltipState.SelectTaperStart;
                if (available && hasRay && applyAction.WasReleasedThisFrame()) m_FirstAnchor = hovered.Key;
                return DrawOverlays(inputDeps,manager,hovered,node,null);
            }
            if (!manager.TryResolveAnchor(m_FirstAnchor.Value,out var source) || !manager.TryGetTaperStart(source,out var start))
            {
                m_FirstAnchor = null;
                return inputDeps;
            }
            var commands = new NativeList<OverlayCommand>(Allocator.TempJob);
            AddFootprint(ref commands,source,SelectedColor);
            float3 destination = default;
            bool hasPoint = false;
            float3 rayDelta = ray.b-ray.a;
            if (hasRay && math.abs(rayDelta.y)>0.0001f)
            {
                float t=(start.y-ray.a.y)/rayDelta.y;
                hasPoint=t>=0&&t<=1;
                destination=ray.a+rayDelta*t;
            }
            float best=1f;
            foreach(var point in manager.GetTaperSnapPoints(source))
            {
                float marker=0.15f;
                AddLine(ref commands,CandidateColor,new Line3.Segment(point-new float3(marker,0,0),point+new float3(marker,0,0)),OutlineWidth*2);
                AddLine(ref commands,CandidateColor,new Line3.Segment(point-new float3(0,0,marker),point+new float3(0,0,marker)),OutlineWidth*2);
                if (!hasRay) continue;
                float t=math.saturate(math.dot(point-ray.a,rayDelta)/math.max(0.0001f,math.lengthsq(rayDelta)));
                float tolerance=math.clamp(math.distance(ray.a,point)*fovTan*0.01f,0.2f,1.2f);
                float score=math.distance(point,ray.a+rayDelta*t)/tolerance;
                if(score<best){best=score;destination=point;hasPoint=true;}
            }
            EdgeGeometry preview=default;
            bool valid=hasPoint && manager.TryBuildManualTaperPreview(source,destination,out preview) &&
                CanApplyEndpoint(manager,source);
            TooltipState=valid?RoadGrassJoinTooltipState.PlaceTaperEnd:RoadGrassJoinTooltipState.SelectTaperEnd;
            if(valid)
            {
                AddPreviewHalf(ref commands,preview,ValidColor);
                if(applyAction.WasReleasedThisFrame() && manager.TryPlaceManualTaper(source,destination,Options.EditCapChoice,Options.EditSurfaceChoice))
                    m_FirstAnchor=null;
            }
            var buffer=m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            var result=new DrawOverlayJob { Commands=commands.AsArray(),Buffer=buffer }
                .Schedule(JobHandle.CombineDependencies(inputDeps,dependencies));
            m_OverlayRenderSystem.AddBufferWriter(result);
            commands.Dispose(result);
            return result;
        }

        private JobHandle DrawSegmentOverlay(JobHandle inputDeps, RoadGrassResolvedMedianAnchor anchor, Color color)
        {
            if (!RoadGrassMedianGeometry.TryGetSegmentStrip(EntityManager, anchor, out var strip)) return inputDeps;
            var commands = new NativeList<OverlayCommand>(Allocator.TempJob);
            AddPreviewHalf(ref commands, strip, color);
            AddLine(ref commands, color, new Line3.Segment(strip.m_Start.m_Left.a, strip.m_Start.m_Right.a), OutlineWidth);
            AddLine(ref commands, color, new Line3.Segment(strip.m_End.m_Left.d, strip.m_End.m_Right.d), OutlineWidth);
            var buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            var result = new DrawOverlayJob { Commands = commands.AsArray(), Buffer = buffer }
                .Schedule(JobHandle.CombineDependencies(inputDeps, dependencies));
            m_OverlayRenderSystem.AddBufferWriter(result);
            commands.Dispose(result);
            return result;
        }

        private bool TryPickAnchor(
            RoadGrassJoinCompositionSystem manager,
            Entity edge,
            Line3.Segment cursorRay,
            float fovTan,
            out RoadGrassResolvedMedianAnchor best)
        {
            var candidates = new List<RoadGrassResolvedMedianAnchor>();
            for (int endpoint = 0; endpoint < 2; endpoint++)
            {
                bool end = endpoint == 1;
                if (!manager.TryGetSelectableAnchors(edge, end, out var anchors))
                {
                    continue;
                }
                candidates.AddRange(anchors);
            }
            return TryPickAnchor(candidates, cursorRay, fovTan, out best);
        }

        private bool TryPickAnchor(
            List<RoadGrassResolvedMedianAnchor> candidates,
            Line3.Segment cursorRay,
            float fovTan,
            out RoadGrassResolvedMedianAnchor best)
        {
            best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                RoadGrassResolvedMedianAnchor anchor = candidates[i];
                if (!TryGetFootprint(
                        anchor,
                        out Bezier4x3 left,
                        out Bezier4x3 right,
                        out _,
                        out float2 range))
                {
                    continue;
                }

                Bezier4x3 leftCut = MathUtils.Cut(left, range);
                Bezier4x3 rightCut = MathUtils.Cut(right, range);
                if (!TryScoreFootprint(
                        leftCut,
                        rightCut,
                        cursorRay,
                        fovTan,
                        out float score) ||
                    score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                best = anchor;
            }
            return best != null;
        }

        private static bool TryScoreFootprint(
            Bezier4x3 left,
            Bezier4x3 right,
            Line3.Segment cursorRay,
            float fovTan,
            out float score)
        {
            Bezier4x3 center = MathUtils.Lerp(left, right, 0.5f);
            float2 curveAndRayT;
            float distance = MathUtils.Distance(center, cursorRay, out curveAndRayT);
            if (!math.isfinite(distance) ||
                !math.all(math.isfinite(curveAndRayT)))
            {
                score = float.MaxValue;
                return false;
            }

            float curveT = math.saturate(curveAndRayT.x);
            float rayT = math.saturate(curveAndRayT.y);
            float3 rayPosition = MathUtils.Position(cursorRay, rayT);
            float cameraDistance = math.distance(cursorRay.a, rayPosition);
            float cursorRadius = math.max(
                MinimumCursorRadius,
                cameraDistance * fovTan * CursorRadiusFovScale);
            float halfWidth = math.distance(
                MathUtils.Position(left, curveT),
                MathUtils.Position(right, curveT)) * 0.5f;
            if (!math.isfinite(cursorRadius) || !math.isfinite(halfWidth) ||
                halfWidth <= 0.005f || distance > halfWidth + cursorRadius)
            {
                score = float.MaxValue;
                return false;
            }

            // Match Traffic's exact curve test, then rank overlapping median
            // strips by distance outside the rendered footprint and finally
            // by their centreline. The tiny ray-depth term keeps stacked
            // elevated roads deterministic without overpowering cursor fit.
            float outsideDistance = math.max(0f, distance - halfWidth);
            score = outsideDistance / math.max(0.01f, cursorRadius) * 10f +
                distance / math.max(0.1f, halfWidth + cursorRadius) +
                rayT * 0.001f;
            return math.isfinite(score);
        }

        private bool TryGetCursorRay(
            out Line3.Segment cursorRay,
            out float fovTan)
        {
            cursorRay = default;
            fovTan = 0f;
            if ((m_ToolRaycastSystem.raycastFlags &
                 (RaycastFlags.DebugDisable | RaycastFlags.UIDisable)) != 0)
            {
                return false;
            }

            if (m_MainCamera == null)
            {
                m_MainCamera = Camera.main;
            }
            if (m_MainCamera == null)
            {
                return false;
            }

            cursorRay = ToolRaycastSystem.CalculateRaycastLine(m_MainCamera);
            fovTan = math.tan(math.radians(m_MainCamera.fieldOfView) * 0.5f);
            return math.all(math.isfinite(cursorRay.a)) &&
                math.all(math.isfinite(cursorRay.b)) &&
                math.isfinite(fovTan) && fovTan > 0f;
        }

        private static bool TryGetUniqueAutomaticTarget(
            RoadGrassJoinCompositionSystem manager,
            RoadGrassResolvedMedianAnchor source,
            out RoadGrassResolvedMedianAnchor target)
        {
            target = null;
            if (manager == null || source == null || source.Node == Entity.Null ||
                manager.IsAnchorLinked(source.Key))
            {
                return false;
            }

            List<RoadGrassResolvedMedianAnchor> candidates =
                manager.GetNodeAnchors(source.Node);
            for (int i = 0; i < candidates.Count; i++)
            {
                RoadGrassResolvedMedianAnchor candidate = candidates[i];
                if (candidate == null || candidate.Key.Equals(source.Key) ||
                    candidate.Key.Edge == source.Key.Edge ||
                    manager.IsAnchorLinked(candidate.Key) ||
                    !manager.TryBuildPairPreview(source, candidate, out _, out _))
                {
                    continue;
                }

                if (target != null)
                {
                    target = null;
                    return false;
                }
                target = candidate;
            }
            return target != null;
        }

        private JobHandle DrawOverlays(
            JobHandle inputDeps,
            RoadGrassJoinCompositionSystem manager,
            RoadGrassResolvedMedianAnchor hovered,
            Entity overlayNode,
            RoadGrassResolvedMedianAnchor automaticTarget)
        {
            if (manager == null ||
                (hovered == null && !m_FirstAnchor.HasValue &&
                 overlayNode == Entity.Null))
            {
                return inputDeps;
            }

            RoadGrassResolvedMedianAnchor first = null;
            if (m_FirstAnchor.HasValue)
            {
                manager.TryResolveAnchor(m_FirstAnchor.Value, out first);
            }
            Entity node = first != null
                ? first.Node
                : (overlayNode != Entity.Null
                    ? overlayNode
                    : hovered?.Node ?? Entity.Null);
            List<RoadGrassResolvedMedianAnchor> candidates = manager.GetNodeAnchors(node);
            var commands = new NativeList<OverlayCommand>(Allocator.TempJob);

            for (int i = 0; i < candidates.Count; i++)
            {
                RoadGrassResolvedMedianAnchor candidate = candidates[i];
                Color color = CandidateColor;
                if (manager.IsAnchorLinked(candidate.Key))
                {
                    color = LinkedColor;
                }
                if (automaticTarget != null &&
                    automaticTarget.Key.Equals(candidate.Key))
                {
                    color = ValidColor;
                }
                if (first != null && first.Key.Equals(candidate.Key))
                {
                    color = SelectedColor;
                }
                if (hovered != null && hovered.Key.Equals(candidate.Key))
                {
                    if (first == null)
                    {
                        color = Options.ActiveMode == MedianControllerMode.Connect ? HoverColor :
                            (CanApplyEndpoint(manager, hovered) ? ValidColor : InvalidColor);
                    }
                    else if (first.Key.Equals(hovered.Key))
                    {
                        color = SelectedColor;
                    }
                    else
                    {
                        color = manager.TryBuildPairPreview(first, hovered, out _, out _)
                            ? ValidColor
                            : InvalidColor;
                    }
                }
                AddFootprint(ref commands, candidate, color);
            }

            if (first != null && hovered != null && !first.Key.Equals(hovered.Key))
            {
                if (manager.TryBuildPairPreview(
                        first,
                        hovered,
                        out EdgeGeometry firstGeometry,
                        out EdgeGeometry secondGeometry))
                {
                    AddPreviewHalf(ref commands, firstGeometry, ValidColor);
                    AddPreviewHalf(ref commands, secondGeometry, ValidColor);
                }
                else if (TryGetNodePoint(first, out float3 a) &&
                         TryGetNodePoint(hovered, out float3 b))
                {
                    AddLine(
                        ref commands,
                        InvalidColor,
                        new Line3.Segment(a, b),
                        OutlineWidth);
                }
            }

            if (commands.Length == 0)
            {
                commands.Dispose();
                return inputDeps;
            }

            OverlayRenderSystem.Buffer buffer =
                m_OverlayRenderSystem.GetBuffer(out JobHandle bufferDependencies);
            JobHandle result = new DrawOverlayJob
            {
                Commands = commands.AsArray(),
                Buffer = buffer
            }.Schedule(JobHandle.CombineDependencies(inputDeps, bufferDependencies));
            m_OverlayRenderSystem.AddBufferWriter(result);
            commands.Dispose(result);
            return result;
        }

        private static void AddPreviewHalf(
            ref NativeList<OverlayCommand> commands,
            EdgeGeometry geometry,
            Color color)
        {
            Color middleColor = new Color(color.r, color.g, color.b, 0.55f);
            AddCurve(ref commands, color, geometry.m_Start.m_Left, OutlineWidth * 1.5f);
            AddCurve(ref commands, color, geometry.m_End.m_Left, OutlineWidth * 1.5f);
            AddCurve(ref commands, color, geometry.m_Start.m_Right, OutlineWidth * 1.5f);
            AddCurve(ref commands, color, geometry.m_End.m_Right, OutlineWidth * 1.5f);
            AddCurve(
                ref commands,
                middleColor,
                MathUtils.Lerp(
                    geometry.m_Start.m_Left,
                    geometry.m_Start.m_Right,
                    0.5f),
                OutlineWidth);
            AddCurve(
                ref commands,
                middleColor,
                MathUtils.Lerp(
                    geometry.m_End.m_Left,
                    geometry.m_End.m_Right,
                    0.5f),
                OutlineWidth);
        }

        private void AddFootprint(
            ref NativeList<OverlayCommand> commands,
            RoadGrassResolvedMedianAnchor anchor,
            Color color)
        {
            if (!TryGetFootprint(
                    anchor,
                    out Bezier4x3 left,
                    out Bezier4x3 right,
                    out _,
                    out float2 range))
            {
                return;
            }
            Bezier4x3 leftCut = MathUtils.Cut(left, range);
            Bezier4x3 rightCut = MathUtils.Cut(right, range);
            AddCurve(ref commands, color, leftCut, OutlineWidth);
            AddCurve(ref commands, color, rightCut, OutlineWidth);
            AddLine(
                ref commands,
                color,
                new Line3.Segment(leftCut.a, rightCut.a),
                OutlineWidth);
            AddLine(
                ref commands,
                color,
                new Line3.Segment(leftCut.d, rightCut.d),
                OutlineWidth);
        }

        private static void AddCurve(
            ref NativeList<OverlayCommand> commands,
            Color color,
            Bezier4x3 curve,
            float width)
        {
            commands.Add(new OverlayCommand
            {
                Type = OverlayCommandType.Curve,
                Color = color,
                Curve = curve,
                Width = width
            });
        }

        private static void AddLine(
            ref NativeList<OverlayCommand> commands,
            Color color,
            Line3.Segment line,
            float width)
        {
            commands.Add(new OverlayCommand
            {
                Type = OverlayCommandType.Line,
                Color = color,
                Line = line,
                Width = width
            });
        }

        private bool TryGetNodePoint(
            RoadGrassResolvedMedianAnchor anchor,
            out float3 point)
        {
            point = default;
            if (!TryGetFootprint(anchor, out _, out _, out Bezier4x3 center, out _))
            {
                return false;
            }
            point = center.d;
            return true;
        }

        private bool TryGetFootprint(
            RoadGrassResolvedMedianAnchor anchor,
            out Bezier4x3 left,
            out Bezier4x3 right,
            out Bezier4x3 center,
            out float2 range)
        {
            left = default;
            right = default;
            center = default;
            range = default;
            if (!RoadGrassMedianGeometry.TryGetOrientedApproach(
                    EntityManager,
                    anchor,
                    out Segment approach))
            {
                return false;
            }
            left = approach.m_Left;
            right = approach.m_Right;
            center = MathUtils.Lerp(left, right, 0.5f);
            float length = math.max(0.01f, MathUtils.Length(center));
            float fraction = math.min(1f, PickLength / length);
            range = new float2(1f - fraction, 1f);
            return true;
        }

        private void UpdateTooltipState(
            RoadGrassJoinCompositionSystem manager,
            RoadGrassResolvedMedianAnchor hovered,
            bool hitRoad,
            bool junctionHasCandidates,
            RoadGrassResolvedMedianAnchor automaticTarget)
        {
            if (Options.ActiveMode != MedianControllerMode.Connect)
            {
                if (Options.ActiveMode == MedianControllerMode.Reset && Options.ResetFilters == MedianResetFilters.None)
                    TooltipState = RoadGrassJoinTooltipState.NoResetFilters;
                else if (hovered == null)
                    TooltipState = RoadGrassJoinTooltipState.SelectEndpoint;
                else if (!CanApplyEndpoint(manager, hovered))
                    TooltipState = Options.ActiveMode == MedianControllerMode.Edit && Options.ExtendIntoIntersection
                        ? RoadGrassJoinTooltipState.UnsupportedTaper : RoadGrassJoinTooltipState.UnsupportedStyle;
                else
                    TooltipState = Options.ActiveMode == MedianControllerMode.Edit
                        ? RoadGrassJoinTooltipState.EditEndpoint : RoadGrassJoinTooltipState.ResetEndpoint;
                return;
            }
            if (!m_FirstAnchor.HasValue)
            {
                TooltipState = hovered == null
                    ? (hitRoad && !junctionHasCandidates
                        ? RoadGrassJoinTooltipState.NoMedian
                        : RoadGrassJoinTooltipState.SelectFirst)
                    : (manager != null && manager.IsAnchorLinked(hovered.Key)
                        ? RoadGrassJoinTooltipState.RemoveLinked
                        : (automaticTarget != null
                            ? RoadGrassJoinTooltipState.AutoConnect
                            : RoadGrassJoinTooltipState.SelectFirstHovered));
                return;
            }

            if (manager == null ||
                !manager.TryResolveAnchor(
                    m_FirstAnchor.Value,
                    out RoadGrassResolvedMedianAnchor first))
            {
                TooltipState = RoadGrassJoinTooltipState.SelectFirst;
                return;
            }
            if (hovered == null)
            {
                TooltipState = RoadGrassJoinTooltipState.SelectSecond;
            }
            else if (first.Key.Equals(hovered.Key))
            {
                TooltipState = RoadGrassJoinTooltipState.SelectedAgain;
            }
            else if (first.Key.Edge == hovered.Key.Edge)
            {
                TooltipState = RoadGrassJoinTooltipState.InvalidSameRoad;
            }
            else if (first.Node == Entity.Null || first.Node != hovered.Node)
            {
                TooltipState = RoadGrassJoinTooltipState.InvalidDifferentJunction;
            }
            else
            {
                TooltipState = manager.TryBuildPairPreview(first, hovered, out _, out _)
                    ? RoadGrassJoinTooltipState.ValidSecond
                    : RoadGrassJoinTooltipState.InvalidGeometry;
            }
        }

        private bool CanApplyEndpoint(RoadGrassJoinCompositionSystem manager, RoadGrassResolvedMedianAnchor anchor)
        {
            return manager != null && (Options.ActiveMode == MedianControllerMode.Edit
                ? manager.CanEditEndpoint(anchor, Options.EditCapChoice, Options.EditSurfaceChoice, Options.ExtendIntoIntersection)
                : manager.CanResetEndpoint(anchor, Options.ResetFilters));
        }

        private bool TryGetHoveredJunction(
            Edge edge,
            float3 hitPosition,
            out Entity node)
        {
            node = Entity.Null;
            float bestDistanceSq = JunctionHoverRadius * JunctionHoverRadius;
            TryUseEndpoint(edge.m_Start, hitPosition, ref node, ref bestDistanceSq);
            if (edge.m_End != edge.m_Start)
            {
                TryUseEndpoint(edge.m_End, hitPosition, ref node, ref bestDistanceSq);
            }
            return node != Entity.Null;
        }

        private void TryUseEndpoint(
            Entity candidate,
            float3 hitPosition,
            ref Entity best,
            ref float bestDistanceSq)
        {
            if (candidate == Entity.Null ||
                !EntityManager.TryGetComponent(candidate, out Node nodeData))
            {
                return;
            }

            float distanceSq = math.distancesq(
                hitPosition.xz,
                nodeData.m_Position.xz);
            if (math.isfinite(distanceSq) && distanceSq <= bestDistanceSq)
            {
                best = candidate;
                bestDistanceSq = distanceSq;
            }
        }

        private bool TryResolveEdge(Entity entity, out Entity edge)
        {
            edge = entity;
            for (int i = 0; i < 4 && edge != Entity.Null && EntityManager.Exists(edge); i++)
            {
                if (EntityManager.HasComponent<Edge>(edge))
                {
                    return true;
                }
                if (!EntityManager.TryGetComponent(edge, out Owner owner))
                {
                    break;
                }
                edge = owner.m_Owner;
            }
            edge = Entity.Null;
            return false;
        }

        private bool TryResolveNode(Entity entity, out Entity node)
        {
            node = entity;
            for (int i = 0; i < 4 && node != Entity.Null && EntityManager.Exists(node); i++)
            {
                if (EntityManager.HasBuffer<ConnectedEdge>(node))
                {
                    return true;
                }
                if (!EntityManager.TryGetComponent(node, out Owner owner))
                {
                    break;
                }
                node = owner.m_Owner;
            }
            node = Entity.Null;
            return false;
        }
    }
}
