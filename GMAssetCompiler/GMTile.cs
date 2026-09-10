using System.IO;

namespace GMAssetCompiler
{
	public class GMTile
	{
		public int X
		{
			get;
			private set;
		}

		public int Y
		{
			get;
			private set;
		}

		public int Index
		{
			get;
			private set;
		}

		public int XO
		{
			get;
			private set;
		}

		public int YO
		{
			get;
			private set;
		}

		public int W
		{
			get;
			private set;
		}

		public int H
		{
			get;
			private set;
		}

		public int Depth
		{
			get;
			private set;
		}

		public int Id
		{
			get;
			private set;
		}

		public double XScale
		{
			get;
			private set;
		}

		public double YScale
		{
			get;
			private set;
		}

		public int Blend
		{
			get;
			private set;
		}

		public double Alpha
		{
			get;
			private set;
		}

		public bool Visible
		{
			get;
			private set;
		}

		// Plain-value constructor for non-GM8.1 loaders (e.g. .gmx projects).
		public GMTile(int _x, int _y, int _index, int _xo, int _yo, int _w, int _h, int _depth, int _id, double _xScale, double _yScale, int _blend, double _alpha)
		{
			X = _x;
			Y = _y;
			Index = _index;
			XO = _xo;
			YO = _yo;
			W = _w;
			H = _h;
			Depth = _depth;
			Id = _id;
			XScale = _xScale;
			YScale = _yScale;
			Blend = _blend;
			Alpha = _alpha;
			Visible = true;
		}

		public GMTile(Stream _stream, int _version)
		{
			X = _stream.ReadInteger();
			Y = _stream.ReadInteger();
			Index = _stream.ReadInteger();
			XO = _stream.ReadInteger();
			YO = _stream.ReadInteger();
			W = _stream.ReadInteger();
			H = _stream.ReadInteger();
			Depth = _stream.ReadInteger();
			Id = _stream.ReadInteger();
			if (_version >= 810)
			{
				XScale = _stream.ReadDouble();
				YScale = _stream.ReadDouble();
				uint num = (uint)_stream.ReadInteger();
				Blend = (int)(num & 0xFFFFFF);
				Alpha = (num >> 24) / 255u;
			}
			else
			{
				XScale = 1.0;
				YScale = 1.0;
				Blend = 16777215;
				Alpha = 1.0;
			}
			Visible = true;
		}
	}
}
