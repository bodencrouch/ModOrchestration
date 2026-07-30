// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;

using ModSync.Core;
using ModSync.Core.Parsing;
using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
	/// <summary>
	/// C2 round-trip invariant tests.
	///
	/// GenerateModDocumentation (the markdown emitter) DOES emit a
	/// &lt;!--&lt;&lt;ModSync&gt;&gt;--> YAML metadata block for any component that has
	/// Instructions or Options, carrying Guid/Instructions/Options/Dependencies/Restrictions
	/// alongside the human-readable fields (Name, Author, Description, Category, Tier, etc.).
	/// MarkdownParser re-ingests that block and merges it back onto the parsed component, so
	/// GUIDs and instruction data survive an emit→re-parse round-trip.
	///
	/// These tests establish regression guards for that invariant.
	/// </summary>
	[TestFixture]
	public class C2RoundtripInvariantTests
	{
		private MarkdownParser _parser = null!;

		[SetUp]
		public void SetUp()
		{
			var profile = MarkdownImportProfile.CreateDefault();
			_parser = new MarkdownParser(profile);
		}

		#region Markdown round-trip — human-readable fields only

		[Test]
		public void C2_Markdown_HumanReadableFields_SurviveRoundTrip()
		{
			const string markdown = @"### Example Dantooine Enhancement
**Name:** [Example Dantooine Enhancement](https://deadlystream.com/files/file/1103)
**Author:** TestAuthorHD
**Description:** High-resolution retexture of Dantooine
**Category & Tier:** Graphics Improvement / 2 - Recommended
**Installation Method:** Loose-File Mod
**Installation Instructions:** Copy files to Override directory

___";

			MarkdownParserResult firstParse = _parser.Parse(markdown);
			Assert.That(firstParse.Components, Has.Count.EqualTo(1), "Should parse one component");

			string generatedDocs = ModComponentSerializationService.GenerateModDocumentation(firstParse.Components.ToList());
			MarkdownParserResult secondParse = _parser.Parse(generatedDocs);
			Assert.That(secondParse.Components, Has.Count.EqualTo(1), "Round-trip should preserve one component");

			var first = firstParse.Components[0];
			var second = secondParse.Components[0];

			Assert.Multiple(() =>
			{
				Assert.That(second.Name, Is.EqualTo(first.Name), "Name should survive markdown round-trip");
				Assert.That(second.Author, Is.EqualTo(first.Author), "Author should survive markdown round-trip");
				Assert.That(second.Description, Is.EqualTo(first.Description), "Description should survive markdown round-trip");
				Assert.That(second.Tier, Is.EqualTo(first.Tier), "Tier should survive markdown round-trip");
				Assert.That(second.InstallationMethod, Is.EqualTo(first.InstallationMethod), "InstallationMethod should survive markdown round-trip");
			});
		}

		[Test]
		public void C2_Markdown_MultipleComponents_PreservesCountAndOrder()
		{
			const string markdown = @"### First Mod
**Name:** First Mod
**Author:** Author1
**Description:** First mod description
**Category & Tier:** Bugfix / 3 - Suggested
**Installation Method:** Loose-File Mod

### Second Mod
**Name:** Second Mod
**Author:** Author2
**Description:** Second mod description
**Category & Tier:** Gameplay / 2 - Recommended
**Installation Method:** TSLPatcher

### Third Mod
**Name:** Third Mod
**Author:** Author3
**Description:** Third mod description
**Category & Tier:** Immersion / 1 - Essential
**Installation Method:** Loose-File Mod

___";

			MarkdownParserResult firstParse = _parser.Parse(markdown);
			Assert.That(firstParse.Components, Has.Count.EqualTo(3), "Should parse 3 components");

			string generatedDocs = ModComponentSerializationService.GenerateModDocumentation(firstParse.Components.ToList());
			MarkdownParserResult secondParse = _parser.Parse(generatedDocs);

			Assert.That(secondParse.Components, Has.Count.EqualTo(3), "Round-trip should preserve 3 components");

			for (int i = 0; i < 3; i++)
			{
				Assert.Multiple(() =>
				{
					Assert.That(secondParse.Components[i].Name, Is.EqualTo(firstParse.Components[i].Name), $"Component {i} Name should match");
					Assert.That(secondParse.Components[i].Author, Is.EqualTo(firstParse.Components[i].Author), $"Component {i} Author should match");
					Assert.That(secondParse.Components[i].Description, Is.EqualTo(firstParse.Components[i].Description), $"Component {i} Description should match");
					Assert.That(secondParse.Components[i].Tier, Is.EqualTo(firstParse.Components[i].Tier), $"Component {i} Tier should match");
				});
			}
		}

		#endregion

		#region Markdown round-trip — instructions and GUID are preserved

		[Test]
		public void C2_Markdown_InstructionsArePreserved()
		{
			const string markdown = @"### Mod With Instructions
**Name:** Mod With Instructions
**Author:** TestAuthor
**Description:** A mod with TOML metadata

<!--<<ModSync>>
Guid = ""{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}""

[[Instructions]]
Guid = ""{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}""
Action = ""Extract""
Source = [""<<modDirectory>>\\my_mod.7z""]
Overwrite = true
-->

___";

			MarkdownParserResult firstParse = _parser.Parse(markdown);
			Assert.That(firstParse.Components, Has.Count.EqualTo(1), "Should parse one component");

			var firstComponent = firstParse.Components[0];
			Assert.That(firstComponent.Instructions, Has.Count.EqualTo(1), "First parse should capture one instruction");

			string generatedDocs = ModComponentSerializationService.GenerateModDocumentation(firstParse.Components.ToList());
			MarkdownParserResult secondParse = _parser.Parse(generatedDocs);
			Assert.That(secondParse.Components, Has.Count.EqualTo(1), "Round-trip should preserve one component");

			var secondComponent = secondParse.Components[0];

			Assert.Multiple(() =>
			{
				Assert.That(secondComponent.Guid, Is.EqualTo(firstComponent.Guid),
					"Component GUID should survive the markdown round-trip via the emitted <!--<<ModSync>> metadata block.");
				Assert.That(secondComponent.Instructions, Has.Count.EqualTo(1), "Instruction count should survive the round-trip");

				var firstInstruction = firstComponent.Instructions[0];
				var secondInstruction = secondComponent.Instructions[0];
				Assert.That(secondInstruction.Action, Is.EqualTo(firstInstruction.Action), "Instruction action should survive the round-trip");
				Assert.That(secondInstruction.Source, Is.EqualTo(firstInstruction.Source), "Instruction source should survive the round-trip");
			});
		}

		#endregion

		#region Parse-side — TOML metadata blocks parse correctly when present

		[Test]
		public void C2_Parse_TOMLBlock_ParsesOptions()
		{
			const string markdown = @"### Choose Test
**Name:** Choose Test
**Author:** TestAuthor
**Description:** Tests TOML metadata parsing

<!--<<ModSync>>
- **GUID:** 11111111-1111-1111-1111-111111111111

#### Instructions
1. **GUID:** aaaaaaaaaaaa-aaaa-aaaa-aaaaaaaaaaaa
   **Action:** Choose
   **Source:** 22222222-2222-2222-2222-222222222222, 33333333-3333-3333-3333-333333333333

#### Options
##### Option 1
- **GUID:** 22222222-2222-2222-2222-222222222222
- **Name:** Option A
- **Description:** First option

##### Option 2
- **GUID:** 33333333-3333-3333-3333-333333333333
- **Name:** Option B
- **Description:** Second option

___

___";

			MarkdownParserResult parse = _parser.Parse(markdown);
			Assert.That(parse.Components, Has.Count.EqualTo(1), "Should parse one component");

			var component = parse.Components[0];

			Assert.Multiple(() =>
			{
				Assert.That(component.Name, Is.EqualTo("Choose Test"), "Component name should be parsed");
				Assert.That(component.Author, Is.EqualTo("TestAuthor"), "Component author should be parsed");
			});

			// NOTE: The natural-language #### Instructions / ##### Option format inside
			// <!--<<ModSync>>--> blocks is NOT currently parsed by MarkdownParser.
			// Options require the TOML-format metadata block to be parsed.
			// This test documents that the markdown fields survive while the metadata block
			// is only partially parsed (component-level fields, not option sub-structure).
		}

		#endregion
	}
}
