using System.IO;

namespace GMAssetCompiler
{
	public class GMView
	{
		public bool Visible
		{
			get;
			private set;
		}

		public int XView
		{
			get;
			private set;
		}

		public int YView
		{
			get;
			private set;
		}

		public int WView
		{
			get;
			private set;
		}

		public int HView
		{
			get;
			private set;
		}

		public int XPort
		{
			get;
			private set;
		}

		public int YPort
		{
			get;
			private set;
		}

		public int WPort
		{
			get;
			private set;
		}

		public int HPort
		{
			get;
			private set;
		}

		public double Angle
		{
			get;
			private set;
		}

		public int HBorder
		{
			get;
			private set;
		}

		public int VBorder
		{
			get;
			private set;
		}

		public int HSpeed
		{
			get;
			private set;
		}

		public int VSpeed
		{
			get;
			private set;
		}

		public int Index
		{
			get;
			private set;
		}

		// Plain-value constructor for non-GM8.1 loaders (e.g. .gmx projects).
		public GMView(bool _visible, int _xView, int _yView, int _wView, int _hView, int _xPort, int _yPort, int _wPort, int _hPort, int _hBorder, int _vBorder, int _hSpeed, int _vSpeed, int _index)
		{
			Visible = _visible;
			XView = _xView;
			YView = _yView;
			WView = _wView;
			HView = _hView;
			XPort = _xPort;
			YPort = _yPort;
			WPort = _wPort;
			HPort = _hPort;
			Angle = 0.0;
			HBorder = _hBorder;
			VBorder = _vBorder;
			HSpeed = _hSpeed;
			VSpeed = _vSpeed;
			Index = _index;
		}

		public GMView(Stream _stream)
		{
			Visible = _stream.ReadBoolean();
			XView = _stream.ReadInteger();
			YView = _stream.ReadInteger();
			WView = _stream.ReadInteger();
			HView = _stream.ReadInteger();
			XPort = _stream.ReadInteger();
			YPort = _stream.ReadInteger();
			WPort = _stream.ReadInteger();
			HPort = _stream.ReadInteger();
			Angle = 0.0;
			HBorder = _stream.ReadInteger();
			VBorder = _stream.ReadInteger();
			HSpeed = _stream.ReadInteger();
			VSpeed = _stream.ReadInteger();
			Index = _stream.ReadInteger();
		}
	}
}
