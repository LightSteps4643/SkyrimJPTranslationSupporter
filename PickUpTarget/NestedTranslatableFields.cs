using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace SkyrimJPStringPatcher.PickUpTarget;

/// <summary>One translatable field found inside a record's nested list/EditorID
/// structure. <see cref="Index"/> means different things per <see cref="DsdType"/>
/// — see remarks below and <see cref="Core.DsdTypeMatching"/> for the matching
/// strategy each type actually uses when checking existing coverage.</summary>
public readonly record struct TranslatableFieldRef(string DsdType, int Index, ITranslatedStringGetter? Field, string EditorId = "");

/// <summary>
/// v0.3.0/v0.4.0 scope expansion, part 2: DSD-supported fields that live inside
/// a NESTED list (quest objectives/log entries, dialogue responses, message
/// buttons, perk effects) or that use a non-FormID identity (GMST's EditorID),
/// as opposed to <see cref="ExtraTranslatableFields"/>'s flat single-property
/// fields. Split into its own file for the same reason ExtraTranslatableFields
/// is separate from PickUpTargetRunner: each record type's extraction logic is
/// independently reviewable/testable, and this is exactly the kind of file a
/// future scope addition (a new nested type) would extend.
/// </summary>
public static class NestedTranslatableFields
{
    public static IEnumerable<TranslatableFieldRef> For(IMajorRecordGetter record)
    {
        switch (record)
        {
            case IQuestGetter quest:
                foreach (var objective in quest.Objectives)
                    yield return new TranslatableFieldRef("QUST NNAM", objective.Index, objective.DisplayText);

                // "QUST CNAM" is DSD's kRuntimeLegacy exception: it matches by
                // ORIGINAL TEXT content at runtime, not by index at all (see
                // DsdTypeMatching.ByOriginalText) — the index here is purely our
                // own bookkeeping so multiple log entries on the same quest
                // don't collide as candidates.
                foreach (var stage in quest.Stages)
                {
                    var position = 0;
                    foreach (var logEntry in stage.LogEntries)
                    {
                        yield return new TranslatableFieldRef("QUST CNAM", stage.Index * 1000 + position, logEntry.Entry);
                        position++;
                    }
                }
                break;

            case IDialogResponsesGetter info:
                yield return new TranslatableFieldRef("INFO RNAM", 0, info.Prompt); // kRuntime2, index always 0
                foreach (var response in info.Responses)
                    yield return new TranslatableFieldRef("INFO NAM1", response.ResponseNumber, response.Text);
                break;

            case IMessageGetter mesg:
                var buttonIndex = 0;
                foreach (var button in mesg.MenuButtons)
                {
                    yield return new TranslatableFieldRef("MESG ITXT", buttonIndex, button.Text);
                    buttonIndex++;
                }
                break;

            case IRegionGetter region:
                yield return new TranslatableFieldRef("REGN RDMP", 0, region.Map?.Name);
                break;

            case IGameSettingStringGetter gmst:
                // kGameSetting matches by EditorID, not FormID (GMST FormIDs are
                // documented as unstable across game versions) — EditorID is
                // threaded through so coverage-checking and output can use it.
                yield return new TranslatableFieldRef("GMST DATA", 0, gmst.Data, gmst.EditorID ?? "");
                break;

            case IPerkGetter perk:
                foreach (var fieldRef in PerkEntryPointFields(perk))
                    yield return fieldRef;
                break;
        }
    }

    /// <summary>
    /// "PERK EPF2" (kButtonText2) / "PERK EPFD" (kPerkVerb) come from the
    /// PerkEntryPointSetText/SelectText effect arms' ButtonLabel/Text fields.
    /// Confirmed via real data (Dragonborn's "DLC2dunTT2IldariPerk",
    /// SetActivateLabel entry point, Text="Rip Heart Out") that this data is
    /// genuinely present and player-visible (the activation-prompt verb), but
    /// NOT which field maps to which of DSD's two type strings. Per
    /// Manager.cpp's constTransContains(), dedup/lookup keys on (FormID,
    /// TranslationType) — "PERK EPF2" and "PERK EPFD" are independent
    /// TranslationTypes, so emitting the SAME text under BOTH labels is safe:
    /// whichever guess is wrong simply matches nothing at its own hook point
    /// and is silently ignored, while the correct one applies. Deliberately
    /// extremely rare in real data (2 records in a real load order use this
    /// arm at all).
    /// </summary>
    private static IEnumerable<TranslatableFieldRef> PerkEntryPointFields(IPerkGetter perk)
    {
        var effectIndex = 0;
        foreach (var effect in perk.Effects)
        {
            ITranslatedStringGetter? buttonLabel = null;
            ITranslatedStringGetter? verbText = null;
            if (effect is IPerkEntryPointSetTextGetter setText)
            {
                buttonLabel = setText.ButtonLabel;
                verbText = setText.Text;
            }
            else if (effect is IPerkEntryPointSelectTextGetter selectText)
            {
                buttonLabel = selectText.ButtonLabel;
            }

            // Distinct indices per field so two genuinely different strings on
            // the same effect never collide as the same candidate.
            if (buttonLabel != null)
            {
                yield return new TranslatableFieldRef("PERK EPF2", effectIndex * 2, buttonLabel);
                yield return new TranslatableFieldRef("PERK EPFD", effectIndex * 2, buttonLabel);
            }
            if (verbText != null)
            {
                yield return new TranslatableFieldRef("PERK EPF2", effectIndex * 2 + 1, verbText);
                yield return new TranslatableFieldRef("PERK EPFD", effectIndex * 2 + 1, verbText);
            }
            effectIndex++;
        }
    }
}
