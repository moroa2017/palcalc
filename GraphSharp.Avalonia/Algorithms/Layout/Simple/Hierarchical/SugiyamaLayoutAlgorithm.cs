﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Avalonia;
using GraphSharp.Algorithms.EdgeRouting;
using QuickGraph;
using QuickGraph.Algorithms.Search;

namespace GraphSharp.Algorithms.Layout.Simple.Hierarchical
{
    public partial class SugiyamaLayoutAlgorithm<TVertex, TEdge, TGraph> : DefaultParameterizedLayoutAlgorithmBase<TVertex, TEdge, TGraph, SugiyamaLayoutParameters>, IEdgeRoutingAlgorithm<TVertex, TEdge, TGraph>
        where TVertex : class
        where TEdge : IEdge<TVertex>
        where TGraph : IVertexAndEdgeListGraph<TVertex, TEdge>
    {
        SoftMutableHierarchicalGraph<SugiVertex, SugiEdge> _graph;

        readonly Func<TEdge, EdgeTypes> _edgePredicate;
        readonly VertexLayerCollection _layers = new VertexLayerCollection();
        double _statusInPercent;

        private const int PercentOfPreparation = 5;
        private const int PercentOfSugiyama = 60;
        private const int PercentOfIncrementalExtension = 30;

        private const string IsolatedVerticesTag = "IsolatedVertices";
        private const string LoopsTag = "Loops";
        private const string GeneralEdgesTag = "GeneralEdges";
        private const string GeneralEdgesBetweenDifferentLayersTag = "GeneralEdgesBetweenDifferentLayers";
        private const string LongEdgesTag = "LongEdges"; //long edges will be replaced with dummy vertices

        public IDictionary<TEdge, Point[]> EdgeRoutes { get; private set; }

        #region Constructors
        public SugiyamaLayoutAlgorithm(
            TGraph visitedGraph,
            IDictionary<TVertex, Size> vertexSizes,
            IDictionary<TVertex, Point> vertexPositions,
            SugiyamaLayoutParameters parameters,
            Func<TEdge, EdgeTypes> edgePredicate )
            : base( visitedGraph, vertexPositions, parameters )
        {
            _edgePredicate = edgePredicate;
            EdgeRoutes = new Dictionary<TEdge, Point[]>();

            ConvertGraph( vertexSizes );
        }

        /// <summary>
        /// Converts the VisitedGraph to the inner type (which is a mutable graph representation).
        /// Wraps the vertices, converts the edges.
        /// </summary>
        private void ConvertGraph( IDictionary<TVertex, Size> vertexSizes )
        {
            //creating the graph with the new type
            _graph = new SoftMutableHierarchicalGraph<SugiVertex, SugiEdge>( true );

            var vertexDict = new Dictionary<TVertex, SugiVertex>();

            //wrapping the vertices
            foreach ( TVertex v in VisitedGraph.Vertices )
            {
                var size = vertexSizes[v];
                size = new Size(size.Width + Parameters.HorizontalGap, size.Height + Parameters.VerticalGap);
                //size.Height += Parameters.VerticalGap;
                //size.Width += Parameters.HorizontalGap;
                var wrapped = new SugiVertex( v, size );

                _graph.AddVertex( wrapped );
                vertexDict[v] = wrapped;
            }

            //creating the new edges
            foreach ( TEdge e in VisitedGraph.Edges )
            {
                var wrapped = new SugiEdge( e, vertexDict[e.Source], vertexDict[e.Target], _edgePredicate( e ) );
                _graph.AddEdge( wrapped );
            }
        }
        #endregion

        #region Filters - used in the preparation phase
        /// <summary>
        /// Removes the cycles from the given graph.
        /// It reverts some edges, so the cycles disappeares.
        /// </summary>
        private void FilterCycles()
        {
            var cycleEdges = new List<SugiEdge>();
            var dfsAlgo = new DepthFirstSearchAlgorithm<SugiVertex, SugiEdge>( _graph );
            dfsAlgo.BackEdge += cycleEdges.Add;
            //non-tree edges selected
            dfsAlgo.Compute();

            //put back the reverted ones
            foreach ( var edge in cycleEdges )
            {
                _graph.RemoveEdge( edge );

                var revertEdge = new SugiEdge( edge.Original, edge.Target, edge.Source, edge.Type );
                _graph.AddEdge( revertEdge );
            }
        }

        private static void FilterIsolatedVertices<TVertexType, TEdgeType>( ISoftMutableGraph<TVertexType, TEdgeType> graph )
            where TEdgeType : class, IEdge<TVertexType>
        {
            graph.HideVerticesIf( v => graph.Degree( v ) == 0, IsolatedVerticesTag );
        }

        private static void FilterLoops<TVertexType, TEdgeType>( ISoftMutableGraph<TVertexType, TEdgeType> graph )
            where TEdgeType : class, IEdge<TVertexType>
            where TVertexType : class
        {
            graph.HideEdgesIf( e => e.Source == e.Target, LoopsTag );
        }

        /// <summary>
        /// First step of the algorithm.
        /// Filters the unappropriate vertices and edges.
        /// </summary>
        private void FiltersAndRemovals()
        {
            //hide every edge but hierarchical ones
            _graph.HideEdges( _graph.GeneralEdges, GeneralEdgesTag );

            //Remove the cycles from the graph
            FilterCycles();

            //remove every isolated vertex
            FilterIsolatedVertices( _graph );

            //filter loops - edges with source = target
            FilterLoops( _graph );
        }
        #endregion

        /// <summary>
        /// Creates the layering of the graph. (Assigns every vertex to a layer.)
        /// </summary>
        private void AssignLayers()
        {
            var lts = new LayeredTopologicalSortAlgorithm<SugiVertex, SugiEdge>( _graph );
            lts.Compute();

            for ( int i = 0; i < lts.LayerCount; i++ )
            {
                var vl = new VertexLayer( _graph, i, lts.Layers[i].ToList() );
                _layers.Add( vl );
            }
        }

        #region Preparation for Sugiyama
        /// <summary>
        /// Minimizes the long of the hierarchical edges by 
        /// putting down the vertices to the layer above  
        /// its descendants.
        /// </summary>
        private void MinimizeHierarchicalEdgeLong()
        {
            if ( !Parameters.MinimizeHierarchicalEdgeLong )
                return;

            for ( int i = _layers.Count - 1; i >= 0; i-- )
            {
                var layer = _layers[i];
                foreach ( var v in layer.ToList() )
                {
                    if ( _graph.OutHierarchicalEdgeCount( v ) == 0 ) continue;

                    //put the vertex above the descendant on the highest layer
                    int newLayerIndex = _graph.OutHierarchicalEdges( v ).Min( edge => edge.Target.LayerIndex - 1 );

                    if ( newLayerIndex != v.LayerIndex )
                    {
                        //we're changing layer
                        layer.Remove( v );
                        _layers[newLayerIndex].Add( v );
                    }
                }
            }
        }

        /// <summary>
        /// Long edges ( span(e) > 1 ) will be replaced by 
        /// span(e) edges (1 edge between every 2 neighbor layer)
        /// and span(e)-1 dummy vertices will be added to graph.
        /// </summary>
        private void ReplaceLongEdges()
        {
            //if an edge goes through multiple layers, we split the edge at every layer and insert a dummy node
            //  (only for the hierarchical edges)
            foreach ( var edge in _graph.HierarchicalEdges.ToArray() )
            {
                int sourceLayerIndex = edge.Source.LayerIndex;
                int targetLayerIndex = edge.Target.LayerIndex;

                if ( Math.Abs( sourceLayerIndex - targetLayerIndex ) <= 1 )
                    continue; //span(e) <= 1, not long edge

                //the edge goes through multiple layers
                edge.IsLongEdge = true;
                _graph.HideEdge( edge, LongEdgesTag );

                //sourcelayer must be above the targetlayer
                if ( targetLayerIndex < sourceLayerIndex )
                {
                    int c = targetLayerIndex;
                    targetLayerIndex = sourceLayerIndex;
                    sourceLayerIndex = c;
                }

                SugiVertex prev = edge.Source;
                for ( int i = sourceLayerIndex + 1; i <= targetLayerIndex; i++ )
                {
                    //the last vertex is the Target, the other ones are dummy vertices
                    SugiVertex dummy;
                    if ( i == targetLayerIndex )
                        dummy = edge.Target;
                    else
                    {
                        dummy = new SugiVertex( null, new Size( 0, 0 ) );
                        _graph.AddVertex( dummy );
                        _layers[i].Add( dummy );
                        edge.DummyVertices.Add( dummy );
                    }
                    _graph.AddEdge( new SugiEdge( edge.Original, prev, dummy, EdgeTypes.Hierarchical ) );
                    prev = dummy;
                }
            }
        }

        private void PrepareForSugiyama()
        {
            MinimizeHierarchicalEdgeLong();

            #region 1) Unhide general edges between vertices participating in the hierarchy
            var analyze = new HashSet<SugiVertex>();
            EdgeAction<SugiVertex, SugiEdge> eeh =
                edge =>
                {
                    analyze.Add( edge.Source );
                    analyze.Add( edge.Target );
                };
            _graph.EdgeUnhidden += eeh;
            _graph.UnhideEdgesIf( e => e.Type == EdgeTypes.General && _graph.ContainsVertex( e.Source ) && _graph.ContainsVertex( e.Target ) );
            _graph.EdgeUnhidden -= eeh;
            #endregion

            #region 2) Move the vertices with general edges if possible
            foreach ( var v in analyze )
            {
                //csak lejjebb lehet rakni
                //csak akkor, ha nincs hierarchikus kimeno el lefele
                //a legkozelebbi lehetseges szintre
                if ( _graph.OutHierarchicalEdgeCount( v ) == 0 )
                {
                    //az altalanos elek kozul az alattalevok kozul a legkozelebbibre kell rakni
                    int newLayerIndex = _layers.Count;
                    foreach ( var e in _graph.InGeneralEdges( v ) )
                    {
                        //nem erdemes tovabb folytatni, lejebb nem kerulhet
                        if ( newLayerIndex == v.LayerIndex ) break;
                        if ( e.Source.LayerIndex >= v.LayerIndex && e.Source.LayerIndex < newLayerIndex )
                            newLayerIndex = e.Source.LayerIndex;
                    }
                    foreach ( var e in _graph.OutGeneralEdges( v ) )
                    {
                        //nem erdemes tovabb folytatni, lejebb nem kerulhet
                        if ( newLayerIndex == v.LayerIndex ) break;
                        if ( e.Target.LayerIndex >= v.LayerIndex && e.Target.LayerIndex < newLayerIndex )
                            newLayerIndex = e.Target.LayerIndex;
                    }
                    if ( newLayerIndex < _layers.Count )
                    {
                        _layers[v.LayerIndex].Remove( v );
                        _layers[newLayerIndex].Add( v );
                    }
                }
            }
            #endregion

            // 3) Hide the general edges between different layers
            _graph.HideEdgesIf( e => ( e.Type == EdgeTypes.General && e.Source.LayerIndex != e.Target.LayerIndex ), GeneralEdgesBetweenDifferentLayersTag );

            //replace long edges with more segments and dummy vertices
            ReplaceLongEdges();

            CopyPositions();
            OnIterationEnded( "Preparation of the positions done." );
        }
        #endregion

        #region Sugiyama Layout
        /// <summary>
        /// Sweeps in one direction in the 1st Phase of the Sugiyama's algorithm.
        /// </summary>
        /// <param name="start">The index of the layer where the sweeping starts.</param>
        /// <param name="end">The index of the layer where the sweeping ends.</param>
        /// <param name="step">Stepcount.</param>
        /// <param name="baryCenter">Kind of the barycentering (Up/Down-barycenter).</param>
        /// <param name="dirty">If this is a dirty sweep</param>
        /// <param name="byRealPosition"></param>
        /// <returns></returns>
        protected bool SugiyamaPhase1Sweep( int start, int end, int step, BaryCenter baryCenter, bool dirty, bool byRealPosition )
        {
            bool hasOptimization = false;
            CrossCount crossCounting = baryCenter == BaryCenter.Up ? CrossCount.Up : CrossCount.Down;
            bool sourceByMeasure = crossCounting == CrossCount.Down;
            for ( int i = start; i != end; i += step )
            {
                var layer = _layers[i];
                int modifiedCrossing = 0;
                int originalCrossing = 0;

                if ( !dirty )
                    //get the count of the edge crossings
                    originalCrossing = layer.CalculateCrossCount( crossCounting );

                //measure the vertices by the given barycenter
                layer.Measure( baryCenter, byRealPosition );

                if ( !dirty )
                    //get the modified crossing count
                    modifiedCrossing = layer.CalculateCrossCount( crossCounting, sourceByMeasure, !sourceByMeasure );

                if ( modifiedCrossing < originalCrossing || dirty )
                {
                    layer.SortByMeasure();
                    hasOptimization = true;
                }

                if ( byRealPosition )
                {
                    HorizontalPositionAssignmentOnLayer( i, baryCenter );
                    CopyPositionsSilent( false );
                }
                else
                {
                    CopyPositions();
                }
                OnIterationEnded( " Phase 1 sweepstep finished [" + baryCenter + "-barycentering on layer " + i + "]" );
            }
            return hasOptimization;
        }

        /// <returns>The index of the layer which is not ordered by <paramref name="baryCenter"/> anymore.
        /// If all of the layers ordered, and phase2 sweep done it returns with -1.</returns>
        protected int SugiyamaPhase2Sweep(int start, int end, int step, BaryCenter baryCenter, bool byRealPosition)
        {
            CrossCount crossCountDirection = baryCenter == BaryCenter.Up ? CrossCount.Up : CrossCount.Down;
            for ( int i = start; i != end; i += step )
            {
                var layer = _layers[i];

                //switch the vertices with the same barycenters, if and only if there will be less barycenters
                layer.Measure( baryCenter, byRealPosition );
                layer.FindBestPermutation( crossCountDirection );

                if ( byRealPosition )
                {
                    HorizontalPositionAssignmentOnLayer( i, baryCenter );
                    CopyPositionsSilent( false );
                }
                else
                {
                    CopyPositions();
                }
                OnIterationEnded( " Phase 2 sweepstep finished [" + baryCenter + "-barycentering on layer " + i + "]" );
                if ( i + step != end )
                {
                    var nextLayer = _layers[i + step];
                    if ( !nextLayer.IsOrderedByBaryCenters( baryCenter, byRealPosition ) )
                        return ( i + step );
                }
            }
            return -1;
        }

        private void SugiyamaDirtyPhase( bool byRealPosition )
        {
            if ( _layers.Count < 2 )
                return;

            const bool dirty = true;
            SugiyamaPhase1Sweep( 1, _layers.Count, 1, BaryCenter.Up, dirty, byRealPosition );
            SugiyamaPhase1Sweep( _layers.Count - 2, -1, -1, BaryCenter.Down, dirty, byRealPosition );
        }

        protected bool SugiyamaPhase1( int startLayerIndex, BaryCenter startBaryCentering, bool byRealPosition )
        {
            if ( _layers.Count < 2 ) return false;

            const bool dirty = false;
            bool sweepDownOptimized = false;

            if ( startBaryCentering == BaryCenter.Up )
            {
                sweepDownOptimized = SugiyamaPhase1Sweep( startLayerIndex == -1 ? 1 : startLayerIndex, _layers.Count, 1, BaryCenter.Up, dirty, byRealPosition );
                startLayerIndex = -1;
            }

            bool sweepUpOptimized = SugiyamaPhase1Sweep( startLayerIndex == -1 ? _layers.Count - 2 : startLayerIndex, -1, -1, BaryCenter.Down, dirty, byRealPosition );

            return sweepUpOptimized || sweepDownOptimized;
        }

        protected void SugiyamaPhase2(out int unorderedLayerIndex, out BaryCenter baryCentering, bool byRealPosition)
        {
            //Sweeping up
            unorderedLayerIndex = SugiyamaPhase2Sweep( 1, _layers.Count, 1, BaryCenter.Up, byRealPosition );
            if ( unorderedLayerIndex != -1 )
            {
                baryCentering = BaryCenter.Up;
                return;
            }

            //Sweeping down
            unorderedLayerIndex = SugiyamaPhase2Sweep( _layers.Count - 2, -1, -1, BaryCenter.Down, byRealPosition );
            baryCentering = BaryCenter.Down;
        }

        private void SugiyamaLayout()
        {
            bool baryCenteringByRealPositions = Parameters.PositionCalculationMethod == PositionCalculationMethodTypes.PositionBased;
            if ( Parameters.DirtyRound )
                //start with a dirty round (sort by barycenters, even if the number of the crossings will rise)
                SugiyamaDirtyPhase( baryCenteringByRealPositions );

            bool changed = true;
            int iteration1Left = Parameters.Phase1IterationCount;
            int iteration2Left = Parameters.Phase2IterationCount;
            double maxIterations = iteration1Left * iteration2Left;

            int startLayerIndex = -1;
            var startBaryCentering = BaryCenter.Up;

            while ( changed && ( iteration1Left > 0 || iteration2Left > 0 ) )
            {
                changed = false;

                //
                // Phase 1 - while there's any optimization
                //
                while ( iteration1Left > 0 && SugiyamaPhase1( startLayerIndex, startBaryCentering, baryCenteringByRealPositions ) )
                {
                    iteration1Left--;
                    changed = true;
                }

                startLayerIndex = -1;
                startBaryCentering = BaryCenter.Up;

                //
                // Phase 2
                //
                if ( iteration2Left > 0 )
                {
                    SugiyamaPhase2( out startLayerIndex, out startBaryCentering, baryCenteringByRealPositions );
                    iteration2Left--;
                }

                // Phase fallback
                if ( startLayerIndex != -1 )
                {
                    iteration1Left = Parameters.Phase1IterationCount;
                    changed = true;
                }

                _statusInPercent += PercentOfSugiyama / maxIterations;
            }

            #region Mark the neighbour vertices connected with associative edges
            foreach ( SugiEdge e in _graph.GeneralEdges )
            {
                int sourceIndex = _layers[e.Source.LayerIndex].IndexOf( e.Source );
                int targetIndex = _layers[e.Target.LayerIndex].IndexOf( e.Target );
                bool shouldBeMarked = e.Source.LayerIndex == e.Target.LayerIndex && Math.Abs( sourceIndex - targetIndex ) == 1;
                if ( shouldBeMarked )
                {
                    if ( sourceIndex < targetIndex )
                    {
                        e.Source.RightGeneralEdgeCount += 1;
                        e.Target.LeftGeneralEdgeCount += 1;
                    }
                    else
                    {
                        e.Target.RightGeneralEdgeCount += 1;
                        e.Source.LeftGeneralEdgeCount += 1;
                    }
                }
            }
            #endregion
        }
        #endregion

        #region Last phase - Horizontal Assignment, edge routing, copying of the positions

        private void AssignPriorities()
        {
            foreach ( var v in _graph.Vertices )
                v.Priority = ( v.IsDummyVertex ? int.MaxValue : _graph.HierarchicalEdgeCountFor( v ) );
        }

        private double CalculateOverlap( SugiVertex a, SugiVertex b, double plusGap = 0)
        {
            return Math.Max( 0, ( ( b.Size.Width + a.Size.Width ) * 0.5 + plusGap + Parameters.HorizontalGap ) - ( b.RealPosition.X - a.RealPosition.X ) );
        }

        protected void HorizontalPositionAssignmentOnLayer( int layerIndex, BaryCenter baryCenter )
        {
            var layer = _layers[layerIndex];

            //compute where the vertices should be placed
            layer.Measure( baryCenter, true );
            layer.CalculateSubPriorities();

            //set the RealPositions to NaN
            foreach (var v in layer)
                v.RealPosition = new Point(float.NaN, v.RealPosition.Y);

            //set the positions in the order of the priorities, start with the lower priorities
            foreach ( var v in from vertex in layer
                               orderby vertex.Priority ascending, vertex.SubPriority ascending
                               select vertex )
            {
                //first set the new position
                v.RealPosition = new Point(v.Measure, v.RealPosition.Y);

                //check if there's any overlap between the actual vertex and the vertices which position has already been set
                SugiVertex v1 = v;
                var alreadySetVertices = layer.Where( vertex => ( !double.IsNaN( vertex.RealPosition.X ) && vertex != v1 ) ).ToArray();

                if ( alreadySetVertices.Length == 0 )
                {
                    //there can't be any overlap
                    continue;
                }

                //get the index of the 'v' vertex between the vertices which position has already been set
                int indexOfV;
                for ( indexOfV = 0;
                      indexOfV < alreadySetVertices.Length && alreadySetVertices[indexOfV].Position < v.Position;
                      indexOfV++ ) { }

                SugiVertex leftNeighbor = null, rightNeighbor = null;
                double leftOverlap = 0, rightOverlap = 0;

                //check the overlap with vertex on the left
                if ( indexOfV > 0 )
                {
                    leftNeighbor = alreadySetVertices[indexOfV - 1];
                    leftOverlap = CalculateOverlap( leftNeighbor, v );
                }
                if ( indexOfV < alreadySetVertices.Length )
                {
                    rightNeighbor = alreadySetVertices[indexOfV];
                    rightOverlap = CalculateOverlap( v, rightNeighbor );
                }

                // ReSharper disable PossibleNullReferenceException
                //only one neighbor overlaps
                if ( leftOverlap > 0 && rightOverlap == 0 )
                {
                    if ( leftNeighbor.Priority == v.Priority )
                    {
                        double leftMove = leftOverlap * 0.5;
                        if ( rightNeighbor != null )
                            rightOverlap = CalculateOverlap( v, rightNeighbor, leftMove );
                        leftNeighbor.RealPosition = new Point(leftNeighbor.RealPosition.X - leftMove, leftNeighbor.RealPosition.Y);
                        v.RealPosition = new Point(v.RealPosition.X + leftMove, v.RealPosition.Y);
                        if ( rightOverlap > 0 )
                        {
                            if ( v.Priority == rightNeighbor.Priority )
                            {
                                double rightMove = rightOverlap * 0.5;
                                rightNeighbor.RealPosition = new Point(rightNeighbor.RealPosition.X + rightMove, rightNeighbor.RealPosition.Y);
                                v.RealPosition = new Point(v.RealPosition.X - rightMove, v.RealPosition.Y);
                                leftNeighbor.RealPosition = new Point(leftNeighbor.RealPosition.X - rightMove, leftNeighbor.RealPosition.Y);
                            }
                            else
                            {
                                rightNeighbor.RealPosition = new Point(rightNeighbor.RealPosition.X + rightOverlap, rightNeighbor.RealPosition.Y);
                            }
                        }
                    }
                    else
                    {
                        leftNeighbor.RealPosition = new Point(leftNeighbor.RealPosition.X - leftOverlap, leftNeighbor.RealPosition.Y);
                    }
                }
                else if ( leftOverlap == 0 && rightOverlap > 0 )
                {
                    if ( v.Priority == rightNeighbor.Priority )
                    {
                        double rightMove = rightOverlap * 0.5;
                        if ( leftNeighbor != null )
                            leftOverlap = CalculateOverlap( leftNeighbor, v, rightMove );
                        rightNeighbor.RealPosition = new Point(rightNeighbor.RealPosition.X + rightMove, rightNeighbor.RealPosition.Y);
                        v.RealPosition = new Point(v.RealPosition.X - rightMove, v.RealPosition.Y);
                        if ( leftOverlap > 0 )
                        {
                            if ( leftNeighbor.Priority == v.Priority )
                            {
                                double leftMove = leftOverlap * 0.5;
                                leftNeighbor.RealPosition = new Point(leftNeighbor.RealPosition.X - leftMove, leftNeighbor.RealPosition.Y);
                                v.RealPosition = new Point(v.RealPosition.X + leftMove, v.RealPosition.Y);
                                rightNeighbor.RealPosition = new Point(rightNeighbor.RealPosition.X + leftMove, rightNeighbor.RealPosition.Y);
                            }
                            else
                            {
                                leftNeighbor.RealPosition = new Point(leftNeighbor.RealPosition.X - leftOverlap, leftNeighbor.RealPosition.Y);
                            }
                        }
                    }
                    else
                    {
                        rightNeighbor.RealPosition = new Point(rightNeighbor.RealPosition.X + rightOverlap, rightNeighbor.RealPosition.Y);
                    }
                }
                else if ( leftOverlap > 0 && rightOverlap > 0 )
                {
                    //if both neighbor overlapped
                    //priorities equals, 1 priority lower, 2 priority lower
                    if ( leftNeighbor.Priority < v.Priority && v.Priority == rightNeighbor.Priority )
                    {
                        double rightMove = rightOverlap * 0.5;
                        rightNeighbor.RealPosition = new Point(rightNeighbor.RealPosition.X + rightMove, rightNeighbor.RealPosition.Y);
                        v.RealPosition = new Point(v.RealPosition.X - rightMove, v.RealPosition.Y);
                        leftNeighbor.RealPosition = new Point(leftNeighbor.RealPosition.X - (leftOverlap + rightMove), leftNeighbor.RealPosition.Y);
                    }
                    else if ( leftNeighbor.Priority == v.Priority && v.Priority > rightNeighbor.Priority )
                    {
                        double leftMove = leftOverlap * 0.5;
                        leftNeighbor.RealPosition = new Point(leftNeighbor.RealPosition.X - leftMove, leftNeighbor.RealPosition.Y);
                        v.RealPosition = new Point(v.RealPosition.X + leftMove, v.RealPosition.Y);
                        rightNeighbor.RealPosition = new Point(rightNeighbor.RealPosition.X + rightOverlap + leftMove, rightNeighbor.RealPosition.Y);
                    }
                    else
                    {
                        //priorities of the neighbors are lower, or equal
                        leftNeighbor.RealPosition = new Point(leftNeighbor.RealPosition.X - leftOverlap, leftNeighbor.RealPosition.Y);
                        rightNeighbor.RealPosition = new Point(rightNeighbor.RealPosition.X + rightOverlap, rightNeighbor.RealPosition.Y);
                    }
                }
                // ReSharper restore PossibleNullReferenceException

                //the vertices on the left side of the leftNeighbor will be moved, if they overlap
                if ( leftOverlap > 0 )
                    for ( int index = indexOfV - 1;
                          index > 0
                          && ( leftOverlap = CalculateOverlap( alreadySetVertices[index - 1], alreadySetVertices[index] ) ) > 0;
                          index-- )
                    {
                        var p = alreadySetVertices[index - 1].RealPosition;

                        alreadySetVertices[index - 1].RealPosition = new Point(
                            p.X - leftOverlap,
                            p.Y
                        );
                    }

                //the vertices on the right side of the rightNeighbor will be moved, if they overlap
                if ( rightOverlap > 0 )
                    for ( int index = indexOfV;
                          index < alreadySetVertices.Length - 1
                          && ( rightOverlap = CalculateOverlap( alreadySetVertices[index], alreadySetVertices[index + 1] ) ) > 0;
                          index++ )
                    {
                        var p = alreadySetVertices[index + 1].RealPosition;

                        alreadySetVertices[index + 1].RealPosition = new Point(
                            p.X + rightOverlap,
                            p.Y
                        );
                    }
            }
        }

        protected void HorizontalPositionAssignmentSweep( int start, int end, int step, BaryCenter baryCenter )
        {
            for ( int i = start; i != end; i += step )
                HorizontalPositionAssignmentOnLayer( i, baryCenter );
        }

        private void HorizontalPositionAssignment()
        {
            //sweeping up & down, assigning the positions for the vertices in the order of the priorities
            //positions computed with the barycenter method, based on the realpositions
            AssignPriorities();

            if ( _layers.Count > 1 )
            {
                HorizontalPositionAssignmentSweep( 1, _layers.Count, 1, BaryCenter.Up );
                HorizontalPositionAssignmentSweep( _layers.Count - 2, -1, -1, BaryCenter.Down );
            }
        }

        private void AssignPositions()
        {
            //initialize positions
            double verticalPos = 0;
            for ( int i = 0; i < _layers.Count; i++ )
            {
                double pos = 0;
                double layerHeight = _layers[i].Height;
                foreach ( var v in _layers[i] )
                {
                    v.RealPosition = new Point(
                        pos,
                        ((i == 0)
                            ? (layerHeight - v.Size.Height)
                            : verticalPos + layerHeight * (float)0.5)
                    );
                    pos += v.Size.Width + Parameters.HorizontalGap;
                }
                verticalPos += layerHeight + Parameters.VerticalGap;
            }

            //assign the horizontal positions
            HorizontalPositionAssignment();
        }

        private void CopyPositionsSilent(bool shouldTranslate = true)
        {
            //calculate the topLeft position
            var translation = new Vector(float.PositiveInfinity, float.PositiveInfinity);
            if (shouldTranslate)
            {
                foreach (SugiVertex v in _graph.Vertices)
                {
                    if (double.IsNaN(v.RealPosition.X) || double.IsNaN(v.RealPosition.Y))
                        continue;

                    translation = new Vector(
                        Math.Min(v.RealPosition.X, translation.X),
                        Math.Min(v.RealPosition.Y, translation.Y)
                    );
                }
                translation *= -1;
                translation = new Vector(
                    translation.X + Parameters.VerticalGap / 2,
                    translation.Y + Parameters.HorizontalGap / 2
                );

                //translate with the topLeft position
                foreach (SugiVertex v in _graph.Vertices)
                    v.RealPosition += translation;
            }
            else
            {
                translation = new Vector(0, 0);
            }

            //copy the positions of the vertices
            VertexPositions.Clear();
            foreach (SugiVertex v in _graph.Vertices)
            {
                if (v.IsDummyVertex)
                    continue;

                Point pos = v.RealPosition;
                if (!shouldTranslate)
                {
                    pos = new Point(
                        pos.X + v.Size.Width * 0.5 + translation.X,
                        pos.Y + v.Size.Height * 0.5 + translation.Y
                    );
                }
                VertexPositions[v.Original] = pos;
            }

            //copy the edge routes
            EdgeRoutes.Clear();
            foreach (SugiEdge e in _graph.HiddenEdges)
            {
                if (!e.IsLongEdge)
                    continue;

                EdgeRoutes[e.Original] =
                        e.IsReverted
                                ? e.DummyVertices.Reverse().Select(dv => dv.RealPosition).ToArray()
                                : e.DummyVertices.Select(dv => dv.RealPosition).ToArray();
            }
        }

        /// <summary>
        /// Copies the coordinates of the vertices to the VertexPositions dictionary.
        /// </summary>
        private void CopyPositions()
        {
            AssignPositions();

            CopyPositionsSilent();
        }
        #endregion

        protected override void InternalCompute()
        {
            // Phase 1 - Filters & Removals
            FiltersAndRemovals();
            _statusInPercent = PercentOfPreparation;

            // Phase 2 - Layer assignment
            AssignLayers();

            // Phase 3 - Crossing reduction
            PrepareForSugiyama();
            SugiyamaLayout();
            _statusInPercent = PercentOfPreparation + PercentOfSugiyama;

            // Phase 4 - Horizontal position assignment
            CopyPositions();
            OnIterationEnded( "Position adjusting finished" );

            // Phase 5 - Incremental extension, add vertices connected with only general edges
            _statusInPercent = PercentOfPreparation + PercentOfSugiyama + PercentOfIncrementalExtension;
            _statusInPercent = 100;
        }

        private void OnIterationEnded( string message )
        {
            OnIterationEnded(0, _statusInPercent, message, true);
        }
    }
}