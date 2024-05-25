using Avalonia;
using System.Collections.Generic;
using System.Windows;

namespace GraphSharp.Algorithms.OverlapRemoval
{
	public abstract class OverlapRemovalAlgorithmBase<TObject, TParam> : AlgorithmBase, IOverlapRemovalAlgorithm<TObject, TParam>
		where TObject : class
		where TParam : IOverlapRemovalParameters
	{
		protected IDictionary<TObject, Rect> OriginalRectangles;
		public IDictionary<TObject, Rect> Rectangles
		{
			get { return OriginalRectangles; }
		}

		public TParam Parameters { get; private set; }

		public IOverlapRemovalParameters GetParameters()
		{
			return Parameters;
		}

		protected List<RectangleWrapper<TObject>> WrappedRectangles;

	    protected OverlapRemovalAlgorithmBase( IDictionary<TObject, Rect> rectangles, TParam parameters )
		{
			OriginalRectangles = rectangles;
			WrappedRectangles = new List<RectangleWrapper<TObject>>();

			int i = 0;
			foreach ( var kvpRect in rectangles )
			{
				WrappedRectangles.Insert( i, new RectangleWrapper<TObject>( kvpRect.Value, kvpRect.Key ) );
				i++;
			}

			Parameters = parameters;
		}

		protected sealed override void InternalCompute()
		{
		    if (WrappedRectangles.Count == 0)
		        return;

			AddGaps();

			RemoveOverlap();

			RemoveGaps();

			foreach ( var r in WrappedRectangles )
				OriginalRectangles[r.Id] = r.Rectangle;
		}

		protected virtual void AddGaps()
		{
			foreach ( var r in WrappedRectangles )
			{
				r.Rectangle = new Rect(
					r.Rectangle.Position,
					new Size(r.Rectangle.Width + Parameters.HorizontalGap, r.Rectangle.Height + Parameters.VerticalGap)
				).Translate(new Vector(-Parameters.HorizontalGap / 2, -Parameters.VerticalGap / 2));
			}
		}

		protected virtual void RemoveGaps()
		{
			foreach ( var r in WrappedRectangles )
			{
				r.Rectangle = new Rect(
					r.Rectangle.Position,
					new Size(
						r.Rectangle.Width - Parameters.HorizontalGap,
						r.Rectangle.Height - Parameters.VerticalGap
					)
				).Translate(new Vector(Parameters.HorizontalGap / 2, Parameters.VerticalGap / 2));
			}
		}

		protected abstract void RemoveOverlap();
	}
}