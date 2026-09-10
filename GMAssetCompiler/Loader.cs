using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace GMAssetCompiler
{
	public class Loader
	{
		public static byte[] g_FileBuffer;

		private static uint[] ms_fastCRC = null;

		public static GMAssets Load(string _name)
		{
			return Load(_name, ChovyUI.eGMTarget.GameMaker81);
		}

		public static GMAssets Load(string _name, ChovyUI.eGMTarget _target)
		{
			if (_target == ChovyUI.eGMTarget.GameMakerStudio14)
			{
				GMAssets gms14Assets = LoadGMS14Project(_name);
				if (gms14Assets != null)
				{
					// IFFSaver.WriteHeader derives the in-game name via
					// Path.GetFileNameWithoutExtension(FileName), which only
					// strips one extension - a real "<name>.project.gmx" path
					// would yield "<name>.project", so synthesize a path whose
					// single extension strips down to the actual project name.
					string projectDir = Path.GetDirectoryName(Path.GetFullPath(_name));
					gms14Assets.FileName = Path.Combine(projectDir, gms14Assets.Name + ".gmx");
				}
				return gms14Assets;
			}

			GMAssets gMAssets = null;
			if (string.Compare(Path.GetExtension(_name), ".psp", true) == 0)
			{
				FileStream fileStream = File.Open(_name, FileMode.Open, FileAccess.Read, FileShare.Read);
				gMAssets = LoadPSP(fileStream);
				fileStream.Close();
			}
			else
			{
				g_FileBuffer = File.ReadAllBytes(_name);
				MemoryStream memoryStream = new MemoryStream(g_FileBuffer, true);
				gMAssets = LoadGMK(memoryStream, _name);
				memoryStream.Close();
			}
			if (gMAssets != null)
			{
				gMAssets.FileName = Path.GetFullPath(_name);
			}
			return gMAssets;
		}

		// GameMaker: Studio 1.4 project loader (.project.gmx, VM target).
		// Unlike the GM8.1 path, there is no embedded/encrypted executable to
		// decrypt: a .gmx project is a plain, uncompressed XML tree (a master
		// "<name>.project.gmx" file plus per-resource-type subfolders), so this
		// reads that XML directly into the same GMAssets graph the rest of the
		// compiler (GML compilation, texture packing, IFFSaver) already knows
		// how to consume unchanged.
		public static GMAssets LoadGMS14Project(string _projectGmxPath)
		{
			string projectDir = Path.GetDirectoryName(Path.GetFullPath(_projectGmxPath));
			XElement root = XDocument.Load(_projectGmxPath).Root;

			string projectName = Path.GetFileName(_projectGmxPath);
			if (projectName.EndsWith(".project.gmx", StringComparison.OrdinalIgnoreCase))
			{
				projectName = projectName.Substring(0, projectName.Length - ".project.gmx".Length);
			}

			GMAssets assets = new GMAssets(projectName, 0, Guid.NewGuid());

			XElement soundsEl = root.Element("sounds");
			if (soundsEl != null)
			{
				foreach (XElement soundRef in soundsEl.Elements("sound"))
				{
					string relPath = soundRef.Value.Trim();
					string name = Path.GetFileName(relPath);
					string soundFile = Path.Combine(projectDir, ToNativePath(relPath) + ".sound.gmx");
					GMSound sound = LoadGMS14Sound(soundFile);
					assets.Sounds.Add(new KeyValuePair<string, GMSound>(name, sound));
				}
			}

			Dictionary<string, int> backgroundIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			XElement backgroundsEl = root.Element("backgrounds");
			if (backgroundsEl != null)
			{
				foreach (XElement backgroundRef in backgroundsEl.Elements("background"))
				{
					string relPath = backgroundRef.Value.Trim();
					string name = Path.GetFileName(relPath);
					string backgroundFile = Path.Combine(projectDir, ToNativePath(relPath) + ".background.gmx");
					GMBackground background = LoadGMS14Background(backgroundFile);
					backgroundIndex[name] = assets.Backgrounds.Count;
					assets.Backgrounds.Add(new KeyValuePair<string, GMBackground>(name, background));
				}
			}

			XElement pathsEl = root.Element("paths");
			if (pathsEl != null)
			{
				foreach (XElement pathRef in pathsEl.Elements("path"))
				{
					string relPath = pathRef.Value.Trim();
					string name = Path.GetFileName(relPath);
					string pathFile = Path.Combine(projectDir, ToNativePath(relPath) + ".path.gmx");
					GMPath path = LoadGMS14Path(pathFile);
					assets.Paths.Add(new KeyValuePair<string, GMPath>(name, path));
				}
			}

			Dictionary<string, int> spriteIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			XElement spritesEl = root.Element("sprites");
			if (spritesEl != null)
			{
				foreach (XElement spriteRef in spritesEl.Elements("sprite"))
				{
					string relPath = spriteRef.Value.Trim();
					string name = Path.GetFileName(relPath);
					string spriteFile = Path.Combine(projectDir, ToNativePath(relPath) + ".sprite.gmx");
					GMSprite sprite = LoadGMS14Sprite(spriteFile);
					spriteIndex[name] = assets.Sprites.Count;
					assets.Sprites.Add(new KeyValuePair<string, GMSprite>(name, sprite));
				}
			}

			XElement scriptsEl = root.Element("scripts");
			if (scriptsEl != null)
			{
				foreach (XElement scriptRef in scriptsEl.Elements("script"))
				{
					string relPath = scriptRef.Value.Trim();
					string name = Path.GetFileNameWithoutExtension(relPath);
					string scriptFile = Path.Combine(projectDir, ToNativePath(relPath));
					string code = File.ReadAllText(scriptFile);
					assets.Scripts.Add(new KeyValuePair<string, GMScript>(name, new GMScript(code)));
				}
			}

			XElement fontsEl = root.Element("fonts");
			if (fontsEl != null)
			{
				foreach (XElement fontRef in fontsEl.Elements("font"))
				{
					string relPath = fontRef.Value.Trim();
					string name = Path.GetFileName(relPath);
					string fontFile = Path.Combine(projectDir, ToNativePath(relPath) + ".font.gmx");
					GMFont font = LoadGMS14Font(fontFile);
					assets.Fonts.Add(new KeyValuePair<string, GMFont>(name, font));
				}
			}

			XElement timelinesEl = root.Element("timelines");
			if (timelinesEl != null)
			{
				foreach (XElement timelineRef in timelinesEl.Elements("timeline"))
				{
					string relPath = timelineRef.Value.Trim();
					string name = Path.GetFileName(relPath);
					string timelineFile = Path.Combine(projectDir, ToNativePath(relPath) + ".timeline.gmx");
					XElement timelineXml = XDocument.Load(timelineFile).Root;
					GMTimeLine timeline = LoadGMS14TimeLine(timelineXml);
					assets.TimeLines.Add(new KeyValuePair<string, GMTimeLine>(name, timeline));
				}
			}

			// Objects are read in two passes: names/indices first, then bodies -
			// so a parentName/objName reference to an object later in the list
			// (or to itself) still resolves, matching how GM8.1's own indices work.
			Dictionary<string, int> objectIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			List<KeyValuePair<string, XElement>> objectDefs = new List<KeyValuePair<string, XElement>>();
			XElement objectsEl = root.Element("objects");
			if (objectsEl != null)
			{
				foreach (XElement objRef in objectsEl.Elements("object"))
				{
					string relPath = objRef.Value.Trim();
					string name = Path.GetFileName(relPath);
					string objFile = Path.Combine(projectDir, ToNativePath(relPath) + ".object.gmx");
					XElement objXml = XDocument.Load(objFile).Root;
					objectIndex[name] = objectDefs.Count;
					objectDefs.Add(new KeyValuePair<string, XElement>(name, objXml));
				}
				foreach (KeyValuePair<string, XElement> def in objectDefs)
				{
					GMObject obj = LoadGMS14Object(def.Value, spriteIndex, objectIndex);
					assets.Objects.Add(new KeyValuePair<string, GMObject>(def.Key, obj));
				}
			}

			XElement roomsEl = root.Element("rooms");
			if (roomsEl != null)
			{
				foreach (XElement roomRef in roomsEl.Elements("room"))
				{
					string relPath = roomRef.Value.Trim();
					string name = Path.GetFileName(relPath);
					string roomFile = Path.Combine(projectDir, ToNativePath(relPath) + ".room.gmx");
					XElement roomXml = XDocument.Load(roomFile).Root;
					GMRoom room = LoadGMS14Room(roomXml, objectIndex, backgroundIndex);
					assets.RoomOrder.Add(assets.Rooms.Count);
					assets.Rooms.Add(new KeyValuePair<string, GMRoom>(name, room));
				}
			}

			TagBackgroundTilesets(assets);
			GMLFunctionValidator.ValidateAssets(assets);
			return assets;
		}

		private static string ToNativePath(string _gmxRelativePath)
		{
			return _gmxRelativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
		}

		private static GMSound LoadGMS14Sound(string _soundFile)
		{
			XElement soundXml = XDocument.Load(_soundFile).Root;
			string soundDir = Path.GetDirectoryName(_soundFile);

			int kind = XInt(soundXml, "kind");
			string extension = XVal(soundXml, "extension");
			string origName = XVal(soundXml, "origname");
			int effects = XInt(soundXml, "effects");
			XElement volumeEl = soundXml.Element("volume");
			double volume = volumeEl != null ? XDouble(volumeEl, "volume", 1.0) : 1.0;
			double pan = XDouble(soundXml, "pan", 0.0);
			bool preload = XBool(soundXml, "preload");

			string dataFileName = XVal(soundXml, "data");
			byte[] data = null;
			if (!string.IsNullOrEmpty(dataFileName))
			{
				string audioFile = Path.Combine(soundDir, "audio", ToNativePath(dataFileName));
				data = File.ReadAllBytes(audioFile);
			}

			return new GMSound(kind, extension, origName, effects, volume, pan, preload, data);
		}

		private static GMBackground LoadGMS14Background(string _backgroundFile)
		{
			XElement backgroundXml = XDocument.Load(_backgroundFile).Root;
			string backgroundDir = Path.GetDirectoryName(_backgroundFile);

			bool tileset = XBool(backgroundXml, "istileset");
			string imageFile = Path.Combine(backgroundDir, ToNativePath(XVal(backgroundXml, "data")));
			GMBitmap32 bitmap = GMBitmap32.FromFile(imageFile);

			return new GMBackground(bitmap.Width, bitmap.Height, true, false, true, tileset, bitmap);
		}

		private static GMPath LoadGMS14Path(string _pathFile)
		{
			XElement pathXml = XDocument.Load(_pathFile).Root;

			int kind = XInt(pathXml, "kind");
			bool closed = XBool(pathXml, "closed");
			int precision = XInt(pathXml, "precision");

			List<GMPathPoint> points = new List<GMPathPoint>();
			XElement pointsEl = pathXml.Element("points");
			if (pointsEl != null)
			{
				foreach (XElement pointEl in pointsEl.Elements("point"))
				{
					string[] parts = pointEl.Value.Trim().Split(',');
					double x = double.Parse(parts[0], CultureInfo.InvariantCulture);
					double y = double.Parse(parts[1], CultureInfo.InvariantCulture);
					double speed = double.Parse(parts[2], CultureInfo.InvariantCulture);
					points.Add(new GMPathPoint(x, y, speed));
				}
			}

			return new GMPath(kind, closed, precision, points);
		}

		private static GMTimeLine LoadGMS14TimeLine(XElement _timelineXml)
		{
			List<KeyValuePair<int, GMEvent>> entries = new List<KeyValuePair<int, GMEvent>>();
			foreach (XElement entryEl in _timelineXml.Elements("entry"))
			{
				int step = XInt(entryEl, "step");
				List<GMAction> actions = new List<GMAction>();
				XElement eventEl = entryEl.Element("event");
				if (eventEl != null)
				{
					foreach (XElement actionEl in eventEl.Elements("action"))
					{
						GMAction action = LoadGMS14Action(actionEl);
						if (action != null)
						{
							actions.Add(action);
						}
					}
				}
				entries.Add(new KeyValuePair<int, GMEvent>(step, new GMEvent(actions)));
			}
			return new GMTimeLine(entries);
		}

		private static GMSprite LoadGMS14Sprite(string _spriteFile)
		{
			XElement spriteXml = XDocument.Load(_spriteFile).Root;
			string spriteDir = Path.GetDirectoryName(_spriteFile);

			int xorig = XInt(spriteXml, "xorig");
			int yorig = XInt(spriteXml, "yorigin");
			int bboxLeft = XInt(spriteXml, "bbox_left");
			int bboxRight = XInt(spriteXml, "bbox_right");
			int bboxTop = XInt(spriteXml, "bbox_top");
			int bboxBottom = XInt(spriteXml, "bbox_bottom");
			int bboxMode = XInt(spriteXml, "bboxmode");
			int colKind = XInt(spriteXml, "colkind");
			bool sepMasks = XBool(spriteXml, "sepmasks");

			List<GMBitmap32> images = new List<GMBitmap32>();
			XElement framesEl = spriteXml.Element("frames");
			if (framesEl != null)
			{
				foreach (XElement frameEl in framesEl.Elements("frame"))
				{
					string framePath = Path.Combine(spriteDir, ToNativePath(frameEl.Value.Trim()));
					images.Add(GMBitmap32.FromFile(framePath));
				}
			}

			bool transparent = true;
			bool colCheck = colKind != 0;
			return new GMSprite(xorig, yorig, images, bboxLeft, bboxRight, bboxTop, bboxBottom, bboxMode, transparent, false, true, colCheck, sepMasks);
		}

		// Unlike GM8.1's own binary format, GMS1.4 already renders and exports
		// the glyph atlas as a plain PNG (white glyphs on transparent, same
		// convention IFFSaver already expects) alongside the .font.gmx XML, so
		// there is no rasterization to reimplement here - just load the image
		// and carry over its own pre-computed per-glyph pixel metrics.
		//
		// One non-obvious wrinkle: the on-device runner indexes a font's
		// glyph list DIRECTLY by raw character code (0-255), not by an
		// offset from First - First/Last are just metadata. GM8.1's own
		// binary format reflects this by always storing exactly 256 glyph
		// slots regardless of the font's actual range (confirmed by
		// GMFont's Stream constructor, which unconditionally loops 256
		// times). A .gmx font's XML only lists the glyphs that actually
		// exist (e.g. 32-127), so those have to be placed at their real
		// character-code index in a full 256-slot list, with everything
		// else left as a blank (all-zero) placeholder glyph - otherwise
		// every character renders as whatever glyph happens to sit at
		// that raw code's offset into a too-short list instead.
		private static GMFont LoadGMS14Font(string _fontFile)
		{
			XElement fontXml = XDocument.Load(_fontFile).Root;
			string fontDir = Path.GetDirectoryName(_fontFile);

			string name = XVal(fontXml, "name");
			int size = XInt(fontXml, "size");
			bool bold = XBool(fontXml, "bold");
			bool italic = XBool(fontXml, "italic");

			GMGlyph blank = new GMGlyph(0, 0, 0, 0, 0, 0);
			GMGlyph[] slots = new GMGlyph[256];
			for (int i = 0; i < 256; i++)
			{
				slots[i] = blank;
			}
			int first = int.MaxValue;
			int last = int.MinValue;
			XElement glyphsEl = fontXml.Element("glyphs");
			if (glyphsEl != null)
			{
				foreach (XElement glyphEl in glyphsEl.Elements("glyph"))
				{
					int character = AttrInt(glyphEl, "character");
					int x = AttrInt(glyphEl, "x");
					int y = AttrInt(glyphEl, "y");
					int w = AttrInt(glyphEl, "w");
					int h = AttrInt(glyphEl, "h");
					int shift = AttrInt(glyphEl, "shift");
					int offset = AttrInt(glyphEl, "offset");
					if (character >= 0 && character < 256)
					{
						slots[character] = new GMGlyph(x, y, w, h, shift, offset);
					}
					if (character < first) first = character;
					if (character > last) last = character;
				}
			}
			List<GMGlyph> glyphs = new List<GMGlyph>(slots);
			if (last < first)
			{
				first = 0;
				last = 0;
			}

			string imageFile = Path.Combine(fontDir, ToNativePath(XVal(fontXml, "image")));
			Bitmap bitmap = new Bitmap(imageFile);

			return new GMFont(name, size, bold, italic, first, last, glyphs, bitmap);
		}

		private static GMObject LoadGMS14Object(XElement _objXml, Dictionary<string, int> _spriteIndex, Dictionary<string, int> _objectIndex)
		{
			string spriteName = XVal(_objXml, "spriteName");
			int spriteIdx = IsUndefined(spriteName) ? -1 : ResolveIndex(_spriteIndex, spriteName);
			bool solid = XBool(_objXml, "solid");
			bool visible = XBool(_objXml, "visible");
			int depth = XInt(_objXml, "depth");
			bool persistent = XBool(_objXml, "persistent");
			string parentName = XVal(_objXml, "parentName");
			int parentIdx = IsUndefined(parentName) ? -1 : ResolveIndex(_objectIndex, parentName);
			string maskName = XVal(_objXml, "maskName");
			int maskIdx = IsUndefined(maskName) ? -1 : ResolveIndex(_spriteIndex, maskName);

			// 12 fixed event-type slots, matching GM8.1's own numbering
			// (Create=0, Destroy=1, Alarm=2, Step=3, Collision=4, Keyboard=5,
			// Mouse=6, Other=7, Draw=8, KeyPress=9, KeyRelease=10, Trigger=11).
			IList<IList<KeyValuePair<int, GMEvent>>> events = new List<IList<KeyValuePair<int, GMEvent>>>();
			for (int i = 0; i < 12; i++)
			{
				events.Add(new List<KeyValuePair<int, GMEvent>>());
			}

			XElement eventsEl = _objXml.Element("events");
			if (eventsEl != null)
			{
				foreach (XElement eventEl in eventsEl.Elements("event"))
				{
					int eventType = AttrInt(eventEl, "eventtype");
					int subEvent = AttrInt(eventEl, "enumb");
					// This PSP runner is a GM8.1-era engine and only understands
					// the single classic Draw event (subevent 0). GMS1.4 added
					// newer Draw sub-events (64=Draw GUI, 65=Resize, 72/73=Begin/
					// End, 76/77=Pre/Post) that this runner's per-frame Draw
					// dispatch has no concept of and never invokes - collapse
					// all of them down to the classic slot so the GML in a
					// GMS1.4-authored Draw event (which now defaults to "Draw
					// GUI" in the IDE, not plain "Draw") actually still runs.
					if (eventType == 8)
					{
						subEvent = 0;
					}
					List<GMAction> actions = new List<GMAction>();
					foreach (XElement actionEl in eventEl.Elements("action"))
					{
						GMAction action = LoadGMS14Action(actionEl);
						if (action != null)
						{
							actions.Add(action);
						}
					}
					if (eventType >= 0 && eventType < events.Count)
					{
						events[eventType].Add(new KeyValuePair<int, GMEvent>(subEvent, new GMEvent(actions)));
					}
				}
			}

			return new GMObject(spriteIdx, solid, visible, depth, persistent, parentIdx, maskIdx, events);
		}

		// Only plain GML code actions (<kind>7</kind>/<exetype>2</exetype>, the
		// shape GameMaker: Studio 1.4 emits for a code block dropped into an
		// event) are supported - drag-and-drop library actions (<kind>0</kind>,
		// with a <functionname> and typed <arguments> instead of a single GML
		// string) have no GML source to extract and are out of scope for this
		// loader. Returning null and skipping them here - rather than
		// mis-wrapping their first argument as if it were GML code, which
		// would either fail to compile or silently compile into a meaningless
		// no-op expression statement - keeps the failure mode "action is
		// missing" instead of "action runs but does something wrong".
		private static GMAction LoadGMS14Action(XElement _actionEl)
		{
			int kind = XInt(_actionEl, "kind");
			if (kind != (int)eAction.ACT_CODE)
			{
				string functionName = XVal(_actionEl, "functionname");
				Console.WriteLine("Warning: skipping unsupported drag-and-drop action '{0}' (kind {1}) - only plain GML code actions are supported by this loader.", string.IsNullOrEmpty(functionName) ? "?" : functionName, kind);
				return null;
			}

			int id = XInt(_actionEl, "id");
			string code = string.Empty;
			XElement argumentsEl = _actionEl.Element("arguments");
			if (argumentsEl != null)
			{
				XElement firstArg = argumentsEl.Elements("argument").FirstOrDefault();
				if (firstArg != null)
				{
					XElement stringEl = firstArg.Element("string");
					if (stringEl != null)
					{
						code = stringEl.Value;
					}
				}
			}
			return new GMAction(id, code);
		}

		private static GMRoom LoadGMS14Room(XElement _roomXml, Dictionary<string, int> _objectIndex, Dictionary<string, int> _backgroundIndex)
		{
			string caption = XVal(_roomXml, "caption");
			int width = XInt(_roomXml, "width");
			int height = XInt(_roomXml, "height");
			int speed = XInt(_roomXml, "speed");
			bool persistent = XBool(_roomXml, "persistent");
			int colour = XInt(_roomXml, "colour");
			bool showColour = XBool(_roomXml, "showcolour");
			string code = XVal(_roomXml, "code");
			bool enableViews = XBool(_roomXml, "enableViews");

			List<GMBack> backgrounds = new List<GMBack>();
			XElement backgroundsEl = _roomXml.Element("backgrounds");
			if (backgroundsEl != null)
			{
				foreach (XElement bgEl in backgroundsEl.Elements("background"))
				{
					bool bgVisible = AttrBool(bgEl, "visible");
					bool bgForeground = AttrBool(bgEl, "foreground");
					int bgX = AttrInt(bgEl, "x");
					int bgY = AttrInt(bgEl, "y");
					bool bgHTiled = AttrBool(bgEl, "htiled");
					bool bgVTiled = AttrBool(bgEl, "vtiled");
					int bgHSpeed = AttrInt(bgEl, "hspeed");
					int bgVSpeed = AttrInt(bgEl, "vspeed");
					bool bgStretch = AttrBool(bgEl, "stretch");
					string bgName = AttrVal(bgEl, "name");
					int bgIndex = IsUndefined(bgName) ? -1 : ResolveIndex(_backgroundIndex, bgName);
					backgrounds.Add(new GMBack(bgVisible, bgForeground, bgIndex, bgX, bgY, bgHTiled, bgVTiled, bgHSpeed, bgVSpeed, bgStretch));
				}
			}

			List<GMView> views = new List<GMView>();
			XElement viewsEl = _roomXml.Element("views");
			if (viewsEl != null)
			{
				foreach (XElement viewEl in viewsEl.Elements("view"))
				{
					bool viewVisible = AttrBool(viewEl, "visible");
					int xview = AttrInt(viewEl, "xview");
					int yview = AttrInt(viewEl, "yview");
					int wview = AttrInt(viewEl, "wview");
					int hview = AttrInt(viewEl, "hview");
					int xport = AttrInt(viewEl, "xport");
					int yport = AttrInt(viewEl, "yport");
					int wport = AttrInt(viewEl, "wport");
					int hport = AttrInt(viewEl, "hport");
					int hborder = AttrInt(viewEl, "hborder");
					int vborder = AttrInt(viewEl, "vborder");
					int hspeed = AttrInt(viewEl, "hspeed");
					int vspeed = AttrInt(viewEl, "vspeed");
					string objName = AttrVal(viewEl, "objName");
					int objIdx = IsUndefined(objName) ? -1 : ResolveIndex(_objectIndex, objName);
					views.Add(new GMView(viewVisible, xview, yview, wview, hview, xport, yport, wport, hport, hborder, vborder, hspeed, vspeed, objIdx));
				}
			}

			List<GMInstance> instances = new List<GMInstance>();
			XElement instancesEl = _roomXml.Element("instances");
			if (instancesEl != null)
			{
				int nextId = 100000;
				foreach (XElement instEl in instancesEl.Elements("instance"))
				{
					int x = AttrInt(instEl, "x");
					int y = AttrInt(instEl, "y");
					string objName = AttrVal(instEl, "objName");
					int objIdx = ResolveIndex(_objectIndex, objName);
					string instCode = AttrVal(instEl, "code");
					double scaleX = AttrDouble(instEl, "scaleX", 1.0);
					double scaleY = AttrDouble(instEl, "scaleY", 1.0);
					uint colourVal = AttrUInt(instEl, "colour", uint.MaxValue);
					double rotation = AttrDouble(instEl, "rotation", 0.0);
					instances.Add(new GMInstance(x, y, objIdx, nextId, instCode, scaleX, scaleY, colourVal, rotation));
					nextId++;
				}
			}

			List<GMTile> tiles = new List<GMTile>();
			XElement tilesEl = _roomXml.Element("tiles");
			if (tilesEl != null)
			{
				foreach (XElement tileEl in tilesEl.Elements("tile"))
				{
					string bgName = AttrVal(tileEl, "bgName");
					int bgIndex = IsUndefined(bgName) ? -1 : ResolveIndex(_backgroundIndex, bgName);
					int x = AttrInt(tileEl, "x");
					int y = AttrInt(tileEl, "y");
					int w = AttrInt(tileEl, "w");
					int h = AttrInt(tileEl, "h");
					int xo = AttrInt(tileEl, "xo");
					int yo = AttrInt(tileEl, "yo");
					int id = AttrInt(tileEl, "id");
					int depth = AttrInt(tileEl, "depth");
					double scaleX = AttrDouble(tileEl, "scaleX", 1.0);
					double scaleY = AttrDouble(tileEl, "scaleY", 1.0);
					uint colourVal = AttrUInt(tileEl, "colour", uint.MaxValue);
					int blend = (int)(colourVal & 0xFFFFFF);
					double alpha = (colourVal >> 24) / 255.0;
					tiles.Add(new GMTile(x, y, bgIndex, xo, yo, w, h, depth, id, scaleX, scaleY, blend, alpha));
				}
			}

			return new GMRoom(caption, width, height, speed, persistent, colour, showColour, code, backgrounds, enableViews, views, instances, tiles);
		}

		private static bool IsUndefined(string _name)
		{
			return string.IsNullOrEmpty(_name) || _name == "<undefined>";
		}

		private static int ResolveIndex(Dictionary<string, int> _map, string _name)
		{
			int result;
			if (_map.TryGetValue(_name, out result))
			{
				return result;
			}
			Console.WriteLine("Warning: unresolved reference '{0}' in GameMaker: Studio 1.4 project", _name);
			return -1;
		}

		private static string XVal(XElement _parent, string _name)
		{
			XElement el = _parent.Element(_name);
			return el != null ? el.Value : string.Empty;
		}

		private static int XInt(XElement _parent, string _name)
		{
			int result;
			return int.TryParse(XVal(_parent, _name), out result) ? result : 0;
		}

		private static bool XBool(XElement _parent, string _name)
		{
			return XInt(_parent, _name) != 0;
		}

		private static double XDouble(XElement _parent, string _name, double _default)
		{
			double result;
			return double.TryParse(XVal(_parent, _name), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : _default;
		}

		private static string AttrVal(XElement _el, string _name)
		{
			XAttribute attr = _el.Attribute(_name);
			return attr != null ? attr.Value : string.Empty;
		}

		private static int AttrInt(XElement _el, string _name)
		{
			int result;
			return int.TryParse(AttrVal(_el, _name), out result) ? result : 0;
		}

		private static bool AttrBool(XElement _el, string _name)
		{
			return AttrInt(_el, _name) != 0;
		}

		private static double AttrDouble(XElement _el, string _name, double _default)
		{
			double result;
			return double.TryParse(AttrVal(_el, _name), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : _default;
		}

		private static uint AttrUInt(XElement _el, string _name, uint _default)
		{
			uint result;
			return uint.TryParse(AttrVal(_el, _name), out result) ? result : _default;
		}

		private static uint[] InitFastCRC()
		{
			if (ms_fastCRC == null)
			{
				uint[] array = new uint[256];
				uint num = 3988292384u;
				for (uint num2 = 0u; num2 < 256; num2++)
				{
					uint num3 = num2;
					for (uint num4 = 8u; num4 != 0; num4--)
					{
						num3 = (((num3 & 1) == 0) ? (num3 >> 1) : ((num3 >> 1) ^ num));
					}
					array[num2] = num3;
				}
				ms_fastCRC = array;
			}
			return ms_fastCRC;
		}

		public static uint CalcCRC(string _text)
		{
			uint[] array = InitFastCRC();
			uint num = uint.MaxValue;
			foreach (char c in _text)
			{
				num = (((num >> 8) & 0xFFFFFF) ^ array[(num ^ (c & 0xFF)) & 0xFF]);
				num = (((num >> 8) & 0xFFFFFF) ^ array[(num ^ (((int)c >> 8) & 0xFF)) & 0xFF]);
			}
			return num;
		}

		public static int CalcCRC(byte[] _buffer)
		{
			uint[] array = InitFastCRC();
			uint num = uint.MaxValue;
			foreach (byte b in _buffer)
			{
				num = (((num >> 8) & 0xFFFFFF) ^ array[(num ^ b) & 0xFF]);
			}
			return (int)num;
		}

		public static uint CalcCRC(byte[] _buffer, int offset)
		{
			uint[] array = InitFastCRC();
			uint num = uint.MaxValue;
			for (int i = offset; i < _buffer.Length; i++)
			{
				num = (((num >> 8) & 0xFFFFFF) ^ array[(num ^ _buffer[i]) & 0xFF]);
			}
			return num;
		}

		public unsafe static uint HashBitmap(Bitmap _image)
		{
			uint[] array = InitFastCRC();
			uint num = uint.MaxValue;
			BitmapData bitmapData = _image.LockBits(new Rectangle(0, 0, _image.Width, _image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
			uint* ptr = (uint*)bitmapData.Scan0.ToPointer();
			uint* ptr2 = ptr;
			uint num4 = *ptr2;
			for (int i = 0; i < _image.Height; i++)
			{
				uint* ptr3 = (uint*)((long)ptr + i * bitmapData.Stride);
				int num2 = 0;
				while (num2 < _image.Width)
				{
					uint num3 = *ptr3;
					num = (((num >> 8) & 0xFFFFFF) ^ array[(num ^ (num3 & 0xFF)) & 0xFF]);
					num = (((num >> 8) & 0xFFFFFF) ^ array[(num ^ ((num3 >> 8) & 0xFF)) & 0xFF]);
					num = (((num >> 8) & 0xFFFFFF) ^ array[(num ^ ((num3 >> 16) & 0xFF)) & 0xFF]);
					num = (((num >> 8) & 0xFFFFFF) ^ array[(num ^ ((num3 >> 24) & 0xFF)) & 0xFF]);
					num2++;
					ptr3++;
				}
			}
			_image.UnlockBits(bitmapData);
			return num;
		}

		private static uint GetUint(ref uint m_z, ref uint m_w)
		{
			m_z = 36969 * (m_z & 0xFFFF) + (m_z >> 16);
			m_w = 18000 * (m_w & 0xFFFF) + (m_w >> 16);
			return (m_z << 16) + (m_w & 0xFFFF);
		}

		public static bool Process_Encrypt(byte[] _filebuffer, int _offset, string _passphrase, uint _crcOriginal)
		{
			uint num = CalcCRC(_passphrase);
			uint m_z = (uint)((_crcOriginal == uint.MaxValue) ? CalcCRC(_filebuffer) : ((int)_crcOriginal));
			int num2 = _offset + 12 + (int)((num & 0xFF) + 6);
			uint m_w = num;
			for (int i = num2; i < _filebuffer.Length - 5; i += 4)
			{
				int num3 = _filebuffer[i] + (_filebuffer[i + 1] << 8) + (_filebuffer[i + 2] << 16) + (_filebuffer[i + 3] << 24);
				num3 ^= (int)GetUint(ref m_z, ref m_w);
				_filebuffer[i] = (byte)(num3 & 0xFF);
				_filebuffer[i + 1] = (byte)((num3 >> 8) & 0xFF);
				_filebuffer[i + 2] = (byte)((num3 >> 16) & 0xFF);
				_filebuffer[i + 3] = (byte)((num3 >> 24) & 0xFF);
			}
			uint num4 = CalcCRC(_filebuffer, _offset + 12);
			if (num4 != _crcOriginal)
			{
				return false;
			}
			return true;
		}

		public static bool CheckForOldVersions(Stream _stream)
		{
			_stream.Seek(1980000L, SeekOrigin.Begin);
			if (_stream.ReadInteger() == 1234321)
			{
				_stream.Seek(1980000L, SeekOrigin.Begin);
			}
			else
			{
				int num = 2000000;
				_stream.Seek(num, SeekOrigin.Begin);
				while (num < _stream.Length)
				{
					int num2 = _stream.ReadInteger();
					if (num2 == 1234321)
					{
						break;
					}
					num += 10000;
					_stream.Seek(num, SeekOrigin.Begin);
				}
				_stream.Seek(num, SeekOrigin.Begin);
			}
			return true;
		}

		public static bool CheckFor8_1(Stream _stream)
		{
			for (int i = 3800000; i + 8 < _stream.Length; i++)
			{
				_stream.Seek(i, SeekOrigin.Begin);
				uint num = (uint)_stream.ReadInteger();
				uint num2 = (uint)_stream.ReadInteger();
				uint num3 = (uint)(((int)num & -16711936) | (int)(num2 & 0xFF00FF));
				uint num4 = (uint)(((int)num2 & -16711936) | (int)(num & 0xFF00FF));
				if (((int)num4 & -65536) != 0)
				{
					num4 = (uint)(i - 1 - 3800004) / 4u;
				}
				if (num3 == 4145283175u)
				{
					uint num5 = (uint)_stream.ReadInteger();
					uint crcOriginal = (uint)_stream.ReadInteger();
					string passphrase = "_MJD" + num5.ToString() + "#RWK";
					i--;
					_stream.Seek(i + 17, SeekOrigin.Begin);
					uint i2 = (uint)(_stream.ReadInteger() ^ (int)num4);
					_stream.Seek(i + 17, SeekOrigin.Begin);
					_stream.WriteInteger((int)i2);
					_stream.Seek(i + 9, SeekOrigin.Begin);
					if (!Process_Encrypt(g_FileBuffer, i + 9, passphrase, crcOriginal))
					{
						return false;
					}
					_stream.Seek(i + 17 - 4, SeekOrigin.Begin);
					_stream.WriteInteger(1234321);
					_stream.Seek(i + 17 - 4, SeekOrigin.Begin);
					return true;
				}
			}
			return false;
		}

		public static void TagBackgroundTilesets(GMAssets _assets)
		{
			foreach (KeyValuePair<string, GMRoom> room in _assets.Rooms)
			{
				GMRoom value = room.Value;
				if (value != null)
				{
					foreach (GMTile tile in value.Tiles)
					{
						_assets.Backgrounds[tile.Index].Value.Tileset = true;
					}
				}
			}
		}

		public static GMAssets LoadGMK(Stream _stream, string _name)
		{
			GMAssets gMAssets = null;
			if (string.Compare(Path.GetExtension(_name), ".gmk", true) != 0)
			{
				if (!CheckFor8_1(_stream))
				{
					bool flag = CheckForOldVersions(_stream);
				}
				gMAssets = new GMAssets(_stream);
			}
			else
			{
				gMAssets = new GMAssets(_stream, true);
			}
			TagBackgroundTilesets(gMAssets);
			return gMAssets;
		}

		public static GMAssets LoadPSP(Stream _stream)
		{
			GMAssets result = null;
			int num = (int)_stream.Length;
			byte[] array = new byte[num];
			_stream.Read(array, 0, num);
			if (array[0] == 70 && array[1] == 79 && array[2] == 82 && array[3] == 77)
			{
				int num2 = array[4] + (array[5] << 8) + (array[6] << 16) + (array[7] << 24);
				if (num2 == num - 8)
				{
					int num3 = 8;
					StringBuilder stringBuilder = new StringBuilder();
					while (num3 < num)
					{
						stringBuilder.Length = 0;
						stringBuilder.Append((char)array[num3]);
						stringBuilder.Append((char)array[num3 + 1]);
						stringBuilder.Append((char)array[num3 + 2]);
						stringBuilder.Append((char)array[num3 + 3]);
						num2 = array[num3 + 4] + (array[num3 + 5] << 8) + (array[num3 + 6] << 16) + (array[num3 + 7] << 24);
						string text = stringBuilder.ToString();
						num3 += 8; //why 8?
						switch (text)
						{
						case "GEN8":
                            Console.WriteLine("DEBUG: READING GEN8 CHUNK!");
							LoadGeneral(text, num2, array, num3);
							break;
						case "OPTN":
                            Console.WriteLine("DEBUG: READING OPTN CHUNK!");
							LoadOptions(text, num2, array, num3);
							break;
						case "SPRT":
                            Console.WriteLine("DEBUG: READING SPRT CHUNK!");
							Load<GMSprite, YYSprite>(text, num2, array, num3);
							break;
						case "SOND":
                            Console.WriteLine("DEBUG: READING SOND CHUNK!");
							Load<GMSound, YYSound>(text, num2, array, num3);
							break;
						case "BGND":
                            Console.WriteLine("DEBUG: READING BGND CHUNK!");
							Load<GMBackground, YYBackground>(text, num2, array, num3);
							break;
						case "PATH":
                            Console.WriteLine("DEBUG: READING PATH CHUNK!");
							Load<GMPath, YYPath>(text, num2, array, num3);
							break;
						case "SCPT":
                            Console.WriteLine("DEBUG: READING SCPT CHUNK!");
							Load<GMScript, YYScript>(text, num2, array, num3);
							break;
						case "FONT":
                            Console.WriteLine("DEBUG: READING FONT CHUNK!");
							//Load<GMFont, YYFont>(text, num2, array, num3);

                            //makes GMAC crash when reading Karoshi game.psp

                            //due to it tries to load Bitmap data
                            //but app legit PSP games use "DDS" as texture format
							break;
						case "TMLN":
                            Console.WriteLine("DEBUG: READING TMLN CHUNK!");
							//Load<GMTimeLine, YYTimeline>(text, num2, array, num3);

                            //also crashes here for no apparent reason!
							break;
						case "OBJT":
                            Console.WriteLine("DEBUG: READING OBJT CHUNK: PREPARE TO CRASH!");
							Load<GMObject, YYObject>(text, num2, array, num3);
							break;
						case "ROOM":
                            Console.WriteLine("DEBUG: READING ROOM CHUNK!");
							Load<GMRoom, YYRoom>(text, num2, array, num3);
							break;
						default:
                            Console.WriteLine("DEBUG: READING UNKN CHUNK!");//chunk is unknown
							Console.WriteLine("unknown Chunk {0}", text);
							break;
                        case "TXTR": break;
                        case "AUDO": break;
                        case "HELP": break;
                        case "EXTN": break;
                        case "DAFL": break;
                        case "TPAG": break;
						case "STRG":
                            Console.WriteLine("DEBUG: READING STRING CHUNK!");
                            //������ ���������� ��� ������ � ��� ��� ���������!!!!!
                            //fuck gamemaker i want its creators to be burned in hell!!!!
							break;
						}
						num3 += num2;
					}
				}
			}
			return result;
		}

		private static string ReadString(int _offsName, byte[] _buffer)
		{
			IntPtr ptr = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, _offsName);
			StringBuilder stringBuilder = new StringBuilder();
			int num = 0;
			while (true)
			{
				byte b = Marshal.ReadByte(ptr, num);
				if (b == 0)
				{
					break;
				}
				stringBuilder.Append((char)b);
				num++;
			}
			return stringBuilder.ToString();
		}

		private static string TypeToString(object _o, byte[] _buffer, int _offset)
		{
			int num = 0;
			IntPtr ptr = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, _offset);
			StringBuilder stringBuilder = new StringBuilder();
			Type type = _o.GetType();
			FieldInfo[] fields = type.GetFields();
			FieldInfo[] array = fields;
			foreach (FieldInfo fieldInfo in array)
			{
				object obj = null;
				object[] customAttributes = fieldInfo.GetCustomAttributes(false);
				foreach (object obj2 in customAttributes)
				{
					if (obj2 != null)
					{
						obj = obj2;
						break;
					}
				}
				if (obj == null)
				{
					stringBuilder.AppendFormat("{0} : {1}", fieldInfo.Name, fieldInfo.GetValue(_o));
				}
				else if (obj is YYStringOffsetAttribute)
				{
					string arg = ReadString((int)fieldInfo.GetValue(_o), _buffer);
					stringBuilder.AppendFormat("{0} : {1}", fieldInfo.Name, arg);
				}
				else if (obj is YYOffsetToAttribute)
				{
					YYOffsetToAttribute yYOffsetToAttribute = obj as YYOffsetToAttribute;
					int num2 = (int)fieldInfo.GetValue(_o);
					IntPtr ptr2 = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, num2);
					object o = Marshal.PtrToStructure(ptr2, yYOffsetToAttribute.ArrayType);
					stringBuilder.Append("{ ");
					stringBuilder.Append(TypeToString(o, _buffer, num2));
					stringBuilder.Append(" },");
				}
				else if (obj is YYArrayCountAttribute)
				{
					YYArrayCountAttribute yYArrayCountAttribute = obj as YYArrayCountAttribute;
					stringBuilder.Append("[ ");
					int num3 = (int)fieldInfo.GetValue(_o);
					int num4 = Marshal.OffsetOf(type, fieldInfo.Name).ToInt32();
					for (int k = 0; k < num3; k++)
					{
						int num5 = Marshal.ReadInt32(ptr, num + num4 + 4 + k * 4);
						IntPtr ptr3 = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, num5);
						object o2 = Marshal.PtrToStructure(ptr3, yYArrayCountAttribute.ArrayType);
						stringBuilder.Append("{ ");
						stringBuilder.Append(TypeToString(o2, _buffer, num5));
						stringBuilder.Append(" },");
					}
					stringBuilder.Append(" ],");
				}
				else if (obj is YYFixedArrayCountAttribute)
				{
					YYFixedArrayCountAttribute yYFixedArrayCountAttribute = obj as YYFixedArrayCountAttribute;
					stringBuilder.Append("[ ");
					int num6 = (int)fieldInfo.GetValue(_o);
					int num7 = Marshal.OffsetOf(type, fieldInfo.Name).ToInt32();
					int num8 = Marshal.SizeOf(yYFixedArrayCountAttribute.ArrayType);
					for (int l = 0; l < num6; l++)
					{
						int num9 = _offset + num + num7 + 4 + l * num8;
						IntPtr ptr4 = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, num9);
						object o3 = Marshal.PtrToStructure(ptr4, yYFixedArrayCountAttribute.ArrayType);
						stringBuilder.Append("{ ");
						stringBuilder.Append(TypeToString(o3, _buffer, num9));
						stringBuilder.Append(" },");
					}
					stringBuilder.Append("]");
				}
				stringBuilder.AppendFormat(", ");
			}
			return stringBuilder.ToString();
		}

		private static List<KeyValuePair<string, G>> Load<G, Y>(string _chunk, int _sz, byte[] _buffer, int _offset)
		{
			List<KeyValuePair<string, G>> result = new List<KeyValuePair<string, G>>();
			int num = _buffer[_offset] + (_buffer[_offset + 1] << 8) + (_buffer[_offset + 2] << 16) + (_buffer[_offset + 3] << 24);
			_offset += 4;
			while (num > 0)
			{
				int num2 = _buffer[_offset] + (_buffer[_offset + 1] << 8) + (_buffer[_offset + 2] << 16) + (_buffer[_offset + 3] << 24);
				if (num2 != 0)
				{
					IntPtr ptr = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, num2);
					Y val = (Y)Marshal.PtrToStructure(ptr, typeof(Y));
					Console.WriteLine("{0}", TypeToString(val, _buffer, num2));
				}
				num--;
				_offset += 4;
			}
			return result;
		}

		private static GMOptions LoadOptions(string _chunk, int _sz, byte[] _buffer, int _offset)
		{
			GMOptions result = null;
			IntPtr ptr = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, _offset);
			YYOptions yYOptions = (YYOptions)Marshal.PtrToStructure(ptr, typeof(YYOptions));
			Console.WriteLine("{0}", TypeToString(yYOptions, _buffer, _offset));
			return result;
		}

		private static void LoadGeneral(string _chunk, int _sz, byte[] _buffer, int _offset)
		{
			IntPtr ptr = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, _offset);
			YYHeader yYHeader = (YYHeader)Marshal.PtrToStructure(ptr, typeof(YYHeader));
			Console.WriteLine("{0}", TypeToString(yYHeader, _buffer, _offset));
		}
	}
}
