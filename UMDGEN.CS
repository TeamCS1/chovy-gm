using GMAssetCompiler;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace UmdGen
{
    class UMDGEN
    {
        public static void WriteString(Stream s, string Str)
        {
            char[] carr = Str.ToCharArray();
            foreach (char c in carr)
            {
                s.WriteByte((byte)c);
            }
        }
        public static void WriteValueStr(Stream s, string Key, string Value)
        {
            WriteString(s, Key + "=");
            WriteString(s, "\"");
            WriteString(s, Value);
            WriteString(s, "\"");
            WriteString(s, "\n");
        }
        public static void WriteValueInt(Stream s, string Key, int Value)
        {
            WriteString(s, Key + "=");
            WriteString(s, Value.ToString());
            WriteString(s, "\n");
        }

        public static void WritePathStr(Stream s, string IsoPath, string PCPath, int Sector, long Size)
        {
            WriteString(s, "\"");
            WriteString(s, IsoPath);
            WriteString(s, "\", ");
            WriteString(s, "\"");
            WriteString(s, PCPath);
            WriteString(s, "\", ");
            WriteString(s, Sector.ToString());
            WriteString(s, ", ");
            WriteString(s, Size.ToString());
            WriteString(s, ", ");
            WriteString(s, "\n");
        }

        public static void CreateUfl(string UflFile, string InputDir)
        {
            FileStream fs = new FileStream(UflFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            fs.SetLength(0);

            string[] FileList = Directory.GetFiles(InputDir, "*", SearchOption.AllDirectories);
            int Sector = 52767;
            foreach (string FileEntry in FileList)
            {
                long FileSize = new FileInfo(FileEntry).Length;
                int SectorSize = (((int)FileSize - ((int)FileSize % 2048)) / 2048) + 1;
                Console.WriteLine(Path.GetFileName(FileEntry) + " on sector: " + Sector.ToString());
                string FilePath = FileEntry.Substring(InputDir.Length).Replace("\\", "/");
                WritePathStr(fs, FilePath, FileEntry, Sector, FileSize);
                Sector += SectorSize;
            }
            fs.Close();
        }
        public static void CreateUmi(string UmiFile)
        {
            FileStream fs = new FileStream(UmiFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            fs.SetLength(0);

            string TitleId = Program.TitleID;
            string Zone = TitleId.Substring(0, 4);
            string Identifier = TitleId.Substring(4, 5);
            string DiscId = Zone + "-" + Identifier;

            WriteString(fs, "[SYS]\n");
            WriteValueStr(fs, "VERSION", "1.00");
            WriteValueStr(fs, "APPLICATION", "PSP GAME");
            WriteValueStr(fs, "LAYER_STRUCT", "SINGLE");
            WriteValueInt(fs, "FORCE16SECTOR", 0);
            WriteValueInt(fs, "LAYER_TRP", 0);
            WriteValueStr(fs, "FILE_LIST", Path.ChangeExtension(UmiFile, "ufl"));
            WriteValueInt(fs, "BOOT", 0);
            WriteValueInt(fs, "UPDATE", 1);
            WriteValueInt(fs, "CRASH_BOOT_FILE", 1);
            WriteString(fs, "[/SYS]\n\n");


            WriteString(fs, "[MDI]\n");
            WriteValueStr(fs, "DISCNAME", DiscId);
            WriteValueStr(fs, "PRODUCER", "Chovy-GM");
            WriteValueStr(fs, "COPYRIGHT", "CBPS");
            WriteValueStr(fs, "REGION_CODE", "WW");
            WriteString(fs, "[/MDI]\n\n");

            WriteString(fs, "[VOL]\n");
            WriteValueStr(fs, "VOLUME", "Chovy-GM");
            WriteValueStr(fs, "PUBLISHER", "CBPS");
            WriteValueStr(fs, "VOLUME SET", "");
            WriteValueStr(fs, "DATA PREPARER", "Chovy-GM");
            WriteString(fs, "[/VOL]\n\n");
            fs.Close();

        }

        public static bool CreateISO(string UmiFile, string OutputDir)
        {
            Process UmdGen = new Process();
            //This would be too simple, sonys software is buggy af and doenst like being run 
            //powershell -c ^&"C:\Users\earsy\Programs\chovy-gm\umdgenc.exe" /g "UmiFile.umi" "C:\Users\earsy\Desktop\Output"
            //UmdGen.StartInfo.FileName = Path.Combine(Application.StartupPath, "umdgenc.exe");
            //UmdGen.StartInfo.Arguments = "/g \"" + Path.GetFileName(UmiFile) + "\" \"" + OutputDir + "\"";
            UmdGen.StartInfo.FileName = Path.Combine(Application.StartupPath, "umdgenc.exe");
            UmdGen.StartInfo.Arguments = "/g \"UmiFile.umi\" \"" + OutputDir + "\"";
            UmdGen.StartInfo.WorkingDirectory = Path.GetDirectoryName(UmiFile);
            UmdGen.StartInfo.CreateNoWindow = true;
            UmdGen.StartInfo.UseShellExecute = false;
            UmdGen.StartInfo.RedirectStandardOutput = true;
            UmdGen.StartInfo.RedirectStandardError = true;
            
            Console.WriteLine(UmdGen.StartInfo.FileName + " " + UmdGen.StartInfo.Arguments);
            try
            {
                UmdGen.Start();
            }
            catch (Exception ex)
            {
                // umdgenc.exe is an external Sony tool, not part of this repo
                // (see CLAUDE.md's "Running" section) - a missing/misplaced
                // copy is a known, expected gap, not a reason to crash the
                // whole app and take the GUI's build-log console down with it
                // before the user can read anything that was printed above.
                // The .psp itself is already written by this point regardless.
                Console.WriteLine("Could not run umdgenc.exe ({0}) - the final ISO was not built, but game.psp was already compiled successfully. umdgenc.exe is an external Sony tool that must be placed next to this exe (see CLAUDE.md) if you need a bootable ISO.", ex.Message);
                return false;
            }
            UmdGen.WaitForExit();
            if (UmdGen.ExitCode != 0)
            {
                Console.WriteLine(UmdGen.StandardOutput.ReadToEnd() + UmdGen.StandardError.ReadToEnd());
                return false;
            }
            UmdGen.Dispose();
            return true;
        }
    }
}
