// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ModSync.Tests.Fixtures
{
    /// <summary>Which game the mock install should imitate.</summary>
    public enum MockKotorGame
    {
        /// <summary>KOTOR 1, PC layout: capitalised <c>Override</c>, <c>rims/</c>, <c>swkotor.exe</c>.</summary>
        Kotor1,

        /// <summary>
        /// KOTOR 2 / TSL, Aspyr Steam layout: content nested under <c>steamassets/</c> with a
        /// lowercase <c>override</c>, <c>localvault</c> and <c>streamvoice</c>.
        /// </summary>
        Kotor2,
    }

    /// <summary>Paths of a generated mock install.</summary>
    public sealed class MockKotorInstallResult
    {
        public MockKotorInstallResult(MockKotorGame game, string installRoot, string contentRoot, string overrideDirectory)
        {
            Game = game;
            InstallRoot = installRoot;
            ContentRoot = contentRoot;
            OverrideDirectory = overrideDirectory;
        }

        /// <summary>Which game was generated.</summary>
        public MockKotorGame Game { get; }

        /// <summary>The directory a user would point ModSync at (the Steam app folder for K2).</summary>
        public string InstallRoot { get; }

        /// <summary>
        /// Where the game content actually lives. Equals <see cref="InstallRoot"/> for K1 and
        /// <c>&lt;InstallRoot&gt;/steamassets</c> for K2.
        /// </summary>
        public string ContentRoot { get; }

        /// <summary>The override directory, with the casing that game uses.</summary>
        public string OverrideDirectory { get; }
    }

    /// <summary>
    /// Generates a synthetic KOTOR install: the smallest tree that ModSync's own detection and
    /// install code accepts as a real game directory.
    /// <para>
    /// Nothing here is copied from a game. Every file is written from a format layout by
    /// <see cref="MockKotorBinaries"/>, and the directory and file names are taken from the
    /// signature lists ModSync itself scores against in
    /// <c>src/ModSync.Core/Utility/PathUtilities.cs</c>.
    /// </para>
    /// </summary>
    public static class MockKotorInstall
    {
        /// <summary>
        /// Files <c>PathUtilities.DetectKotor2Version</c> counts to classify a KOTOR 2 install as
        /// the Aspyr build. It needs 70% of them, so the mock writes all of them.
        /// Kept verbatim, including the extension-less last entry.
        /// </summary>
        private static readonly string[] AspyrOverrideSignatures =
        {
            "cntrl_ps3_eng.tga", "cntrl_ps3_fre.tga", "cntrl_ps3_ger.tga", "cntrl_ps3_ita.tga",
            "cntrl_ps3_spa.tga", "cntrl_xb360_eng.tga", "cntrl_xb360_fre.tga", "cntrl_xb360_ger.tga",
            "cntrl_xb360_ita.tga", "cntrl_xb360_spa.tga", "cus_button_a.tga", "cus_button_aps.tga",
            "cus_button_b.tga", "cus_button_bps.tga", "cus_button_x.tga", "cus_button_xps.tga",
            "cus_button_y.tga", "cus_button_yps.tga", "cus_gpad_bg.tga", "cus_gpad_fper.tga",
            "cus_gpad_fper2.tga", "cus_gpad_gen.tga", "cus_gpad_gen2.tga", "cus_gpad_hand.tga",
            "cus_gpad_hand2.tga", "cus_gpad_help.tga", "cus_gpad_help2.tga", "cus_gpad_map.tga",
            "cus_gpad_map2.tga", "cus_gpad_save.tga", "cus_gpad_save2.tga", "cus_gpad_solo.tga",
            "cus_gpad_solo2.tga", "cus_gpad_solox.tga", "cus_gpad_solox2.tga", "cus_gpad_ste.tga",
            "cus_gpad_ste2.tga", "cus_gpad_ste3.tga", "custom.txt", "custpnl_p.gui",
            "d2xfnt_d16x16b.tga", "d2xfont16x16b_ps.tga", "d2xfont16x16b.tga", "d3xfnt_d16x16b.tga",
            "d3xfont16x16b_ps.tga", "d3xfont16x16b.tga", "diafnt16x16b_ps.tga", "dialogfont16x16b.tga",
            "equip_p.gui", "fx_step_splash.MDL", "gamepad.txt", "gui_scroll.wav",
            "handmaiden.DLG", "lbl_miscroll_op",
        };

        private static readonly string[] Kotor1Bifs =
        {
            "2da.bif", "gui.bif", "items.bif", "layouts.bif", "models.bif",
            "party.bif", "player.bif", "scripts.bif", "sounds.bif", "templates.bif",
        };

        private static readonly string[] Kotor2Bifs =
        {
            "2da.bif", "dialogs.bif", "gui.bif", "layouts.bif", "models.bif",
            "scripts.bif", "sounds.bif", "templates.bif",
        };

        /// <summary>The dialog.tlk strings the mock ships. Small, but a real string table.</summary>
        private static readonly string[] BaselineTlkStrings =
        {
            "Bad Strref",
            "ModSync mock talk table entry 1",
            "ModSync mock talk table entry 2",
            "ModSync mock talk table entry 3",
        };

        /// <summary>
        /// Creates a mock install under <paramref name="installRoot"/>, deleting anything already
        /// there. Returns the generated paths.
        /// </summary>
        public static MockKotorInstallResult Create(string installRoot, MockKotorGame game)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                throw new ArgumentException("An install root is required.", nameof(installRoot));
            }

            string root = Path.GetFullPath(installRoot);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            Directory.CreateDirectory(root);

            return game == MockKotorGame.Kotor1
                ? CreateKotor1(root)
                : CreateKotor2(root);
        }

        private static MockKotorInstallResult CreateKotor1(string root)
        {
            string overrideDir = Path.Combine(root, "Override");

            CreateDirectories(
                root,
                "data",
                "lips",
                "miles",
                "modules",
                "movies",
                "Override",
                "rims",
                "streammusic",
                "streamsounds",
                "streamwaves",
                "TexturePacks",
                "utils/swupdateskins");

            // PathUtilities.DetectGame scores these thirteen paths for KOTOR 1.
            WriteTextMarker(Path.Combine(root, "swkotor.exe"), "KOTOR 1 executable");
            WriteIni(Path.Combine(root, "swkotor.ini"), "Game Options", new[] { "FullScreen=1", "Width=800", "Height=600" });
            WriteTextMarker(Path.Combine(root, "32370_install.vdf"), "Steam depot manifest");
            MockKotorBinaries.WriteOpaqueStub(Path.Combine(root, "miles", "mssds3d.m3d"), "Miles 3D audio provider");
            MockKotorBinaries.WriteOpaqueStub(Path.Combine(root, "miles", "msssoft.m3d"), "Miles software audio provider");

            WriteBifsAndKey(root, Kotor1Bifs);
            MockKotorBinaries.WriteTlk(Path.Combine(root, "dialog.tlk"), BaselineTlkStrings);
            MockKotorBinaries.WriteEmptyErf(Path.Combine(root, "patch.erf"), "ERF ");

            foreach (string module in new[] { "global.mod", "legal.mod", "mainmenu.mod" })
            {
                MockKotorBinaries.WriteEmptyErf(Path.Combine(root, "modules", module), "MOD ");
            }

            foreach (string module in new[] { "danm13.rim", "danm13_s.rim", "end_m01aa.rim", "end_m01aa_s.rim" })
            {
                MockKotorBinaries.WriteEmptyRim(Path.Combine(root, "modules", module));
            }

            foreach (string rim in new[] { "chargen.rim", "chargendx.rim", "global.rim", "legal.rim", "legaldx.rim", "mainmenu.rim", "mainmenudx.rim" })
            {
                MockKotorBinaries.WriteEmptyRim(Path.Combine(root, "rims", rim));
            }

            WriteBaselineOverride(overrideDir);

            return new MockKotorInstallResult(MockKotorGame.Kotor1, root, root, overrideDir);
        }

        private static MockKotorInstallResult CreateKotor2(string root)
        {
            // Aspyr's Steam build keeps game content one level down. PathUtilities
            // .ResolveInstallGameDirectory rewrites the parent to this folder, so the mock has to
            // reproduce the nesting for that rewrite to be exercised at all.
            string content = Path.Combine(root, "steamassets");
            string overrideDir = Path.Combine(content, "override");

            CreateDirectories(
                content,
                "data",
                "lips",
                "localvault",
                "miles",
                "modules",
                "movies",
                "override",
                "streammusic",
                "streamsounds",
                "streamvoice",
                "texturepacks");

            // The Linux Steam build ships a native launcher at the app root and no PC executable.
            WriteTextMarker(Path.Combine(root, "kotor2"), "Native launcher");
            WriteTextMarker(Path.Combine(root, "com.aspyr.kotor2.version.json"), "{\"version\":\"mock\"}");

            // PathUtilities.DetectGame scores these paths for KOTOR 2.
            WriteTextMarker(Path.Combine(content, "swkotor2.exe"), "KOTOR 2 executable");
            WriteIni(Path.Combine(content, "swkotor2.ini"), "Game Options", new[] { "FullScreen=1", "Width=800", "Height=600" });
            MockKotorBinaries.WriteOpaqueStub(Path.Combine(content, "miles", "binkawin.asi"), "Bink video ASI");
            MockKotorBinaries.WriteOpaqueStub(Path.Combine(content, "miles", "mssds3d.flt"), "Miles 3D audio filter");
            MockKotorBinaries.WriteOpaqueStub(Path.Combine(content, "miles", "mssdolby.flt"), "Miles Dolby filter");
            MockKotorBinaries.WriteOpaqueStub(Path.Combine(content, "miles", "mssogg.asi"), "Miles Ogg decoder");
            MockKotorBinaries.WriteOpaqueStub(Path.Combine(content, "localvault", "test.bic"), "Character vault entry");
            MockKotorBinaries.WriteOpaqueStub(Path.Combine(content, "localvault", "testold.bic"), "Character vault entry");

            WriteBifsAndKey(content, Kotor2Bifs);
            MockKotorBinaries.WriteTlk(Path.Combine(content, "dialog.tlk"), BaselineTlkStrings);

            foreach (string module in new[] { "001ebo.mod", "101per.mod" })
            {
                MockKotorBinaries.WriteEmptyErf(Path.Combine(content, "modules", module), "MOD ");
            }

            foreach (string module in new[] { "001ebo.rim", "001ebo_s.rim", "101per.rim", "101per_s.rim" })
            {
                MockKotorBinaries.WriteEmptyRim(Path.Combine(content, "modules", module));
            }

            WriteBaselineOverride(overrideDir);
            WriteAspyrOverrideSignatures(overrideDir);

            return new MockKotorInstallResult(MockKotorGame.Kotor2, root, content, overrideDir);
        }

        /// <summary>
        /// Writes the override files a mod can realistically patch. These are the targets the
        /// synthetic TSLPatcher fixture edits, which keeps the mock free of BIF-backed game data.
        /// </summary>
        private static void WriteBaselineOverride(string overrideDir)
        {
            Directory.CreateDirectory(overrideDir);

            MockKotorBinaries.Write2da(
                Path.Combine(overrideDir, "mock_baseline.2da"),
                new[] { "label", "value" },
                new IReadOnlyList<string>[]
                {
                    new[] { "baseline_first", "1" },
                    new[] { "baseline_second", "2" },
                });
        }

        private static void WriteAspyrOverrideSignatures(string overrideDir)
        {
            foreach (string name in AspyrOverrideSignatures)
            {
                string path = Path.Combine(overrideDir, name);
                string extension = Path.GetExtension(name).ToUpperInvariant();

                switch (extension)
                {
                    case ".TGA":
                        MockKotorBinaries.WriteTga(path);
                        break;
                    case ".GUI":
                        MockKotorBinaries.WriteEmptyGff(path, "GUI ");
                        break;
                    case ".DLG":
                        MockKotorBinaries.WriteEmptyGff(path, "DLG ");
                        break;
                    case ".WAV":
                        MockKotorBinaries.WriteWav(path);
                        break;
                    case ".TXT":
                        File.WriteAllText(path, "ModSync mock Aspyr control mapping\n");
                        break;
                    default:
                        // .MDL and the extension-less entry: nothing in the install path parses these.
                        MockKotorBinaries.WriteOpaqueStub(path, name);
                        break;
                }
            }
        }

        private static void WriteBifsAndKey(string contentRoot, IReadOnlyList<string> bifNames)
        {
            string dataDir = Path.Combine(contentRoot, "data");
            Directory.CreateDirectory(dataDir);

            var relativePaths = new List<string>(bifNames.Count);
            var sizes = new List<long>(bifNames.Count);

            foreach (string name in bifNames)
            {
                string path = Path.Combine(dataDir, name);
                MockKotorBinaries.WriteEmptyBif(path);
                relativePaths.Add("data\\" + name);
                sizes.Add(new FileInfo(path).Length);
            }

            MockKotorBinaries.WriteKey(Path.Combine(contentRoot, "chitin.key"), relativePaths, sizes);
        }

        private static void CreateDirectories(string root, params string[] relativePaths)
        {
            foreach (string relative in relativePaths)
            {
                Directory.CreateDirectory(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        private static void WriteTextMarker(string path, string label)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, "ModSync synthetic placeholder: " + label + "\n");
        }

        private static void WriteIni(string path, string section, IReadOnlyList<string> entries)
        {
            File.WriteAllText(path, "[" + section + "]\n" + string.Join("\n", entries) + "\n");
        }

        /// <summary>
        /// Command-line front end, reached through <c>ModSync.Tests</c>'s <c>make-mock-kotor</c> verb
        /// so CI can build the tree without running a test.
        /// </summary>
        public static int RunCli(string[] args)
        {
            string outputRoot = null;
            MockKotorGame? game = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out":
                    case "-o":
                        outputRoot = i + 1 < args.Length ? args[++i] : null;
                        break;
                    case "--game":
                    case "-g":
                        string value = i + 1 < args.Length ? args[++i] : null;
                        if (string.Equals(value, "k1", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "kotor1", StringComparison.OrdinalIgnoreCase))
                        {
                            game = MockKotorGame.Kotor1;
                        }
                        else if (string.Equals(value, "k2", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "kotor2", StringComparison.OrdinalIgnoreCase))
                        {
                            game = MockKotorGame.Kotor2;
                        }
                        else
                        {
                            Console.Error.WriteLine("make-mock-kotor: --game must be k1 or k2.");
                            return 2;
                        }

                        break;
                    case "-h":
                    case "--help":
                        PrintUsage();
                        return 0;
                    default:
                        Console.Error.WriteLine("make-mock-kotor: unknown argument '" + args[i] + "'.");
                        PrintUsage();
                        return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(outputRoot) || game is null)
            {
                Console.Error.WriteLine("make-mock-kotor: --out and --game are both required.");
                PrintUsage();
                return 2;
            }

            MockKotorInstallResult result = Create(outputRoot, game.Value);
            int fileCount = Directory.GetFiles(result.InstallRoot, "*", SearchOption.AllDirectories).Length;

            Console.WriteLine("game=" + result.Game);
            Console.WriteLine("installRoot=" + result.InstallRoot);
            Console.WriteLine("contentRoot=" + result.ContentRoot);
            Console.WriteLine("overrideDirectory=" + result.OverrideDirectory);
            Console.WriteLine("files=" + fileCount.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: make-mock-kotor --game <k1|k2> --out <directory>");
            Console.WriteLine();
            Console.WriteLine("Generates a synthetic KOTOR install tree. Contains no game data.");
            Console.WriteLine("The output directory is deleted and recreated.");
        }

        /// <summary>Convenience for tests: every file under a directory, relative and sorted.</summary>
        public static IReadOnlyList<string> ListFiles(string root)
        {
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
    }
}
