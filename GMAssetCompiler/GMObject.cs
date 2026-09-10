using System.Collections.Generic;
using System.IO;
using System;

namespace GMAssetCompiler
{
	public class GMObject
	{
		public int SpriteIndex
		{
			get;
			private set;
		}

		public bool Solid
		{
			get;
			private set;
		}

		public bool Visible
		{
			get;
			private set;
		}

		public int Depth
		{
			get;
			private set;
		}

		public bool Persistent
		{
			get;
			private set;
		}

		public int Parent
		{
			get;
			private set;
		}

		public int Mask
		{
			get;
			private set;
		}

		public IList<IList<KeyValuePair<int, GMEvent>>> Events
		{
			get;
			private set;
		}


		// Plain-value constructor for non-GM8.1 loaders (e.g. .gmx projects).
		// _events must already be shaped as GM8.1's fixed 12 event-type "slots"
		// (index = event type, per eKind.cs), each a list of (subevent number,
		// GMEvent) pairs - the same shape IFFSaver.WriteObjects already expects,
		// so it needs no changes to consume either source.
		public GMObject(int _spriteIndex, bool _solid, bool _visible, int _depth, bool _persistent, int _parent, int _mask, IList<IList<KeyValuePair<int, GMEvent>>> _events)
		{
			SpriteIndex = _spriteIndex;
			Solid = _solid;
			Visible = _visible;
			Depth = _depth;
			Persistent = _persistent;
			Parent = _parent;
			Mask = _mask;
			Events = _events;
		}

		public GMObject(GMAssets _a, Stream _stream)
		{
			int num = _stream.ReadInteger();
			if (num != 400 && num != 430 && num != 820)
			{
				return;
			}
			SpriteIndex = _stream.ReadInteger();
			Solid = _stream.ReadBoolean();
			Visible = _stream.ReadBoolean();
			Depth = _stream.ReadInteger();
			Persistent = _stream.ReadBoolean();
			Parent = _stream.ReadInteger();
			Mask = _stream.ReadInteger();
			int num2 = 8;
			if (num == 430 || num >= 820)
			{
				num2 = _stream.ReadInteger();
			}
			Events = new List<IList<KeyValuePair<int, GMEvent>>>(num2);
			for (int i = 0; i <= num2; i++)
			{
				List<KeyValuePair<int, GMEvent>> list = new List<KeyValuePair<int, GMEvent>>();
				int num3;
				do
				{
					num3 = _stream.ReadInteger();
					if (num3 >= 0)
					{
						GMEvent value = new GMEvent(_a, _stream);
						KeyValuePair<int, GMEvent> item = new KeyValuePair<int, GMEvent>(num3, value);
						list.Add(item);
					}
				}
				while (num3 >= 0);
				Events.Add(list);
			}
            Console.WriteLine("DEBUG: num var: " + num.ToString() + " !");
		}
	}
}
