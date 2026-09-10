using System.IO;
using System.Text;

namespace GMAssetCompiler
{
	public class GMHelp
	{
		public int BackgroundColour
		{
			get;
			private set;
		}

		public bool Mimic
		{
			get;
			private set;
		}

		public string Caption
		{
			get;
			private set;
		}

		public int Left
		{
			get;
			private set;
		}

		public int Top
		{
			get;
			private set;
		}

		public int Width
		{
			get;
			private set;
		}

		public int Height
		{
			get;
			private set;
		}

		public bool Border
		{
			get;
			private set;
		}

		public bool Sizable
		{
			get;
			private set;
		}

		public bool OnTop
		{
			get;
			private set;
		}

		public bool Modal
		{
			get;
			private set;
		}

		public string Text
		{
			get;
			private set;
		}

		// Plain-value constructor for non-GM8.1 loaders (e.g. .gmx projects),
		// which have no equivalent "game help" dialog resource in their XML.
		public GMHelp()
		{
			BackgroundColour = 0;
			Mimic = true;
			Caption = string.Empty;
			Left = 0;
			Top = 0;
			Width = 640;
			Height = 480;
			Border = true;
			Sizable = true;
			OnTop = false;
			Modal = false;
			Text = string.Empty;
		}

		public GMHelp(GMAssets _a, Stream _stream)
		{
			int num = _stream.ReadInteger();
			if (num == 800)
			{
				_stream = _stream.ReadStreamC();
			}
			BackgroundColour = _stream.ReadInteger();
			Mimic = _stream.ReadBoolean();
			Caption = _stream.ReadString();
			Left = _stream.ReadInteger();
			Top = _stream.ReadInteger();
			Width = _stream.ReadInteger();
			Height = _stream.ReadInteger();
			Border = _stream.ReadBoolean();
			Sizable = _stream.ReadBoolean();
			OnTop = _stream.ReadBoolean();
			Modal = _stream.ReadBoolean();
			byte[] array = null;
			switch (num)
			{
			case 600:
				array = _stream.ReadCompressedStream();
				break;
			case 800:
				array = _stream.ReadStream();
				break;
			}
			if (array != null)
			{
				MemoryStream memoryStream = new MemoryStream(array);
				StringBuilder stringBuilder = new StringBuilder();
				while (memoryStream.Position != memoryStream.Length)
				{
					char value = (char)memoryStream.ReadByte();
					stringBuilder.Append(value);
				}
				Text = stringBuilder.ToString();
			}
		}
	}
}
