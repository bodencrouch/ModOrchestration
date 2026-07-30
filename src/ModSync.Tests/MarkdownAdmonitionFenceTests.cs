// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using ModSync.Core.Parsing;

using NUnit.Framework;

namespace ModSync.Tests
{
	/// <summary>
	/// Covers U1 of docs/plans/2026-07-30-001-feat-lossless-roundtrip-universal-pipeline-plan.md:
	/// generalized admonition-fence (:::note/:::warning/:::tip) parsing across any guide field,
	/// matching the live mod-builds guide format (used since 2025-10-20/21) rather than only the
	/// two previously-hardcoded field names.
	/// </summary>
	[TestFixture]
	public class MarkdownAdmonitionFenceTests
	{
		private static MarkdownParserResult Parse(string markdown)
		{
			var profile = MarkdownImportProfile.CreateDefault();
			var parser = new MarkdownParser(profile);
			return parser.Parse(markdown);
		}

		[Test]
		public void FenceStyle_InstallationInstructions_ParsesSameAsBoldInline()
		{
			const string fenced = @"## Mod List

### Fenced Mod
**Name:** Fenced Mod
**Author:** TestAuthor
**Description:** A mod using the current admonition-fence format

:::note
Installation Instructions
:   Move the file to your Override folder.
:::

___";

			const string boldInline = @"## Mod List

### Bold Mod
**Name:** Bold Mod
**Author:** TestAuthor
**Description:** A mod using the older bold-inline format
**Installation Instructions:** Move the file to your Override folder.

___";

			MarkdownParserResult fencedResult = Parse(fenced);
			MarkdownParserResult boldResult = Parse(boldInline);

			Assert.That(fencedResult.Components, Has.Count.EqualTo(1));
			Assert.That(boldResult.Components, Has.Count.EqualTo(1));
			Assert.That(fencedResult.Components[0].Directions, Is.EqualTo(boldResult.Components[0].Directions));
			Assert.That(fencedResult.Components[0].Directions, Is.EqualTo("Move the file to your Override folder."));
		}

		[Test]
		public void FenceStyle_MultiLineBody_StripsDefinitionListPrefixFromEveryLine()
		{
			const string markdown = @"## Mod List

### Multiline Fence Mod
**Name:** Multiline Fence Mod
**Author:** TestAuthor
**Description:** Tests multi-line fence stripping

:::note
Installation Instructions
:   Move everything from the Straight Fixes folder to your Override.
:   Delete the old .tpc files first if present.
:::

___";

			MarkdownParserResult result = Parse(markdown);

			Assert.That(result.Components, Has.Count.EqualTo(1));
			string directions = result.Components[0].Directions;
			Assert.That(directions, Does.Not.Contain(":   "));
			Assert.That(directions, Does.Contain("Move everything from the Straight Fixes folder to your Override."));
			Assert.That(directions, Does.Contain("Delete the old .tpc files first if present."));
		}

		[Test]
		public void FenceStyle_AppliesToPreviouslyUnsupportedField()
		{
			// Description never had a hardcoded fence alternative before this generalization.
			const string markdown = @"## Mod List

### Description Fence Mod
**Name:** Description Fence Mod
**Author:** TestAuthor

:::tip
Description
:   High-resolution retexture of the Ebon Hawk interior.
:::

___";

			MarkdownParserResult result = Parse(markdown);

			Assert.That(result.Components, Has.Count.EqualTo(1));
			Assert.That(result.Components[0].Description, Is.EqualTo("High-resolution retexture of the Ebon Hawk interior."));
		}

		[Test]
		public void FenceStyle_KnownBugsAcceptsAnyAdmonitionType()
		{
			// The original hardcode paired :::warning with Known Bugs specifically; confirm a
			// mismatched admonition type (e.g. :::note wrapping Known Bugs) still parses,
			// since guide authors are not guaranteed to follow that pairing consistently.
			const string markdown = @"## Mod List

### Any Admonition Mod
**Name:** Any Admonition Mod
**Author:** TestAuthor
**Description:** Tests admonition-type flexibility

:::note
Known Bugs
:   Some texture seams may be visible in bright lighting.
:::

___";

			MarkdownParserResult result = Parse(markdown);

			Assert.That(result.Components, Has.Count.EqualTo(1));
			Assert.That(result.Components[0].KnownBugs, Is.EqualTo("Some texture seams may be visible in bright lighting."));
		}

		[Test]
		public void MixedDocument_FenceAndBoldInlineFieldsBothParseInSameComponent()
		{
			const string markdown = @"## Mod List

### Mixed Style Mod
**Name:** Mixed Style Mod
**Author:** TestAuthor
**Description:** Bold-inline description, fenced instructions

:::warning
Installation Instructions
:   Run the patcher twice, selecting Option A both times.
:::

___";

			MarkdownParserResult result = Parse(markdown);

			Assert.That(result.Components, Has.Count.EqualTo(1));
			Assert.That(result.Components[0].Description, Is.EqualTo("Bold-inline description, fenced instructions"));
			Assert.That(result.Components[0].Directions, Is.EqualTo("Run the patcher twice, selecting Option A both times."));
		}

		[Test]
		public void UnclosedFence_DoesNotThrow_DegradesToUnrecognizedField()
		{
			const string markdown = @"## Mod List

### Unclosed Fence Mod
**Name:** Unclosed Fence Mod
**Author:** TestAuthor
**Description:** Tests malformed fence resilience

:::note
Installation Instructions
:   This fence is never closed.

___";

			Assert.DoesNotThrow(() => Parse(markdown));
		}
	}
}
