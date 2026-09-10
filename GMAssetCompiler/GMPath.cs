using System.Collections.Generic;
using System.IO;

namespace GMAssetCompiler
{
	public class GMPath
	{
		public int Kind
		{
			get;
			private set;
		}

		public bool Closed
		{
			get;
			private set;
		}

		public int Precision
		{
			get;
			private set;
		}

		public IList<GMPathPoint> Points
		{
			get;
			private set;
		}

		// Plain-value constructor for non-GM8.1 loaders (e.g. .gmx projects).
		public GMPath(int _kind, bool _closed, int _precision, IList<GMPathPoint> _points)
		{
			Kind = _kind;
			Closed = _closed;
			Precision = _precision;
			Points = _points;
		}

		public GMPath(GMAssets _a, Stream _s)
		{
			_s.ReadInteger();
			Kind = _s.ReadInteger();
			Closed = _s.ReadBoolean();
			Precision = _s.ReadInteger();
			Points = new List<GMPathPoint>();
			int num = _s.ReadInteger();
			for (int i = 0; i < num; i++)
			{
				double x = _s.ReadDouble();
				double y = _s.ReadDouble();
				double speed = _s.ReadDouble();
				Points.Add(new GMPathPoint(x, y, speed));
			}
		}
	}
}
