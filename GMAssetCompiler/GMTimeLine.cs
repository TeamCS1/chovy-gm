using System.Collections.Generic;
using System.IO;

namespace GMAssetCompiler
{
	public class GMTimeLine
	{
		public IList<KeyValuePair<int, GMEvent>> Entries
		{
			get;
			private set;
		}

		// Plain-value constructor for non-GM8.1 loaders (e.g. .gmx projects).
		public GMTimeLine(IList<KeyValuePair<int, GMEvent>> _entries)
		{
			Entries = _entries;
		}

		public GMTimeLine(GMAssets _a, Stream _stream)
		{
			_stream.ReadInteger();
			int num = _stream.ReadInteger();
			Entries = new List<KeyValuePair<int, GMEvent>>(num);
			for (int i = 0; i < num; i++)
			{
				int key = _stream.ReadInteger();
				GMEvent value = new GMEvent(_a, _stream);
				Entries.Add(new KeyValuePair<int, GMEvent>(key, value));
			}
		}
	}
}
