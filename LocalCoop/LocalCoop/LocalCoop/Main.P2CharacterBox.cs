using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SplitScreen
{
    public static partial class Main
    {
        static readonly FieldInfo s_characterNameInputField =
            typeof(CharacterBox).GetField("nameInputfield", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_characterColorGroupField =
            typeof(CharacterBox).GetField("tabGroupColorScheme", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo s_characterUnlockedFromBeginningField =
            typeof(CharacterBox).GetField("charactersUnlockedFromBeginning", BindingFlags.Instance | BindingFlags.NonPublic);

        static GameObject _p2CharacterBoxGo;
        static CharacterBox _p2CharacterBoxClone;
        static InputField _p2CharacterName;
        static TabGroup _p2CharacterGroup;
        static TabGroup _p2OutfitGroup;
        static RGD_Settings_Character _p2MenuCharacter;

        internal static void EnsureP2CharacterBox(CharacterBox source)
        {
            if (source == null || source.name.StartsWith("P2_")) return;
            if (_p2CharacterBoxGo != null)
            {
                _p2CharacterBoxGo.SetActive(source.IsOpen);
                RefreshP2CharacterBox();
                return;
            }

            _p2CharacterBoxGo = Object.Instantiate(source.gameObject, source.transform.parent, false);
            _p2CharacterBoxGo.name = "P2_" + source.gameObject.name;
            SanitizeClonedUiRoot(_p2CharacterBoxGo);
            _p2CharacterBoxClone = _p2CharacterBoxGo.GetComponent<CharacterBox>();
            if (_p2CharacterBoxClone != null) _p2CharacterBoxClone.enabled = false;

            PositionP2CharacterBox(source.transform as RectTransform, _p2CharacterBoxGo.transform as RectTransform);

            _p2CharacterName = s_characterNameInputField.GetValue(_p2CharacterBoxClone) as InputField;
            _p2CharacterGroup = _p2CharacterBoxClone != null ? _p2CharacterBoxClone.tabGroupCharacter : null;
            _p2OutfitGroup = s_characterColorGroupField.GetValue(_p2CharacterBoxClone) as TabGroup;

            BindP2CharacterButtons();
            RefreshP2CharacterBox();
            _p2CharacterBoxGo.SetActive(source.IsOpen);
        }

        internal static void HideP2CharacterBox()
        {
            SaveP2CharacterBox();
            if (_p2CharacterBoxGo != null) _p2CharacterBoxGo.SetActive(false);
        }

        static void PositionP2CharacterBox(RectTransform source, RectTransform clone)
        {
            if (source == null || clone == null) return;
            clone.anchorMin = source.anchorMin;
            clone.anchorMax = source.anchorMax;
            clone.pivot = source.pivot;
            clone.sizeDelta = source.sizeDelta;
            clone.localScale = source.localScale;
            clone.anchoredPosition = source.anchoredPosition + new Vector2(source.rect.width + 24f, 0f);
        }

        static void BindP2CharacterButtons()
        {
            if (_p2CharacterName != null)
            {
                _p2CharacterName.onEndEdit.RemoveAllListeners();
                _p2CharacterName.onEndEdit.AddListener(_ => SaveP2CharacterBox());
                _p2CharacterName.onValueChanged.RemoveAllListeners();
                _p2CharacterName.onValueChanged.AddListener(v =>
                {
                    if (_p2MenuCharacter != null) _p2MenuCharacter.Name = string.IsNullOrWhiteSpace(v) ? "P2_GAMEPAD" : v;
                });
            }

            if (_p2CharacterGroup?.tabButtons != null)
            {
                foreach (var tab in _p2CharacterGroup.tabButtons)
                {
                    if (tab == null || tab.button == null) continue;
                    var captured = tab;
                    captured.button.onClick.RemoveAllListeners();
                    captured.button.onClick.AddListener(() => SelectP2Character(captured.tabIndex));
                }
            }

            if (_p2OutfitGroup?.tabButtons != null)
            {
                foreach (var tab in _p2OutfitGroup.tabButtons)
                {
                    if (tab == null || tab.button == null) continue;
                    var captured = tab;
                    captured.button.onClick.RemoveAllListeners();
                    captured.button.onClick.AddListener(() => SelectP2Outfit(captured.tabIndex));
                }
            }
        }

        static void RefreshP2CharacterBox()
        {
            if (_p2CharacterBoxClone == null) return;
            _p2MenuCharacter = P2CharacterSettings.LoadOrDefault();
            if (_p2CharacterName != null)
                _p2CharacterName.text = string.IsNullOrWhiteSpace(_p2MenuCharacter.Name) ? "P2_GAMEPAD" : _p2MenuCharacter.Name;

            RefreshP2CharacterInteractability();
            SelectP2Character(_p2MenuCharacter.ModelIndex, save: false);
            SelectP2Outfit(_p2MenuCharacter.OutfitIndex, save: false);
        }

        static void RefreshP2CharacterInteractability()
        {
            if (_p2CharacterGroup?.tabButtons == null) return;
            var unlocked = CharacterManager.GetUnlockedCharacterIndexes();
            var fromBeginning = s_characterUnlockedFromBeginningField.GetValue(_p2CharacterBoxClone) as List<int>;
            if (fromBeginning != null) unlocked.AddRangeUniqueOnly(fromBeginning);
            foreach (var tab in _p2CharacterGroup.tabButtons)
                if (tab != null) tab.SetInteractability(unlocked.Contains(tab.tabIndex));
        }

        static void SelectP2Character(int index, bool save = true)
        {
            if (_p2MenuCharacter == null) _p2MenuCharacter = P2CharacterSettings.LoadOrDefault();
            if (!IsP2CharacterUnlocked(index)) return;
            _p2MenuCharacter.ModelIndex = index;
            if (_p2CharacterGroup != null) _p2CharacterGroup.SelectTab(index);
            NormalizeP2TabVisuals(_p2CharacterGroup, index);
            RefreshP2OutfitSprites(index);
            ApplyP2OutfitPreview(_p2MenuCharacter.OutfitIndex);
            if (save) SaveP2CharacterBox();
        }

        static void SelectP2Outfit(int index, bool save = true)
        {
            if (_p2MenuCharacter == null) _p2MenuCharacter = P2CharacterSettings.LoadOrDefault();
            _p2MenuCharacter.OutfitIndex = Mathf.Max(0, index);
            if (_p2OutfitGroup != null) _p2OutfitGroup.SelectTab(_p2MenuCharacter.OutfitIndex);
            NormalizeP2TabVisuals(_p2OutfitGroup, _p2MenuCharacter.OutfitIndex);
            ApplyP2OutfitPreview(_p2MenuCharacter.OutfitIndex);
            if (save) SaveP2CharacterBox();
        }

        static void NormalizeP2TabVisuals(TabGroup group, int selectedIndex)
        {
            if (group?.tabButtons == null) return;
            foreach (var tab in group.tabButtons)
            {
                if (tab == null) continue;
                if (tab.tabIndex == selectedIndex)
                    tab.OnPointerEnter();
                else
                    tab.OnPointerExit(forceExit: true);
            }
        }

        static bool IsP2CharacterUnlocked(int index)
        {
            var unlocked = CharacterManager.GetUnlockedCharacterIndexes();
            var fromBeginning = s_characterUnlockedFromBeginningField.GetValue(_p2CharacterBoxClone) as List<int>;
            if (fromBeginning != null) unlocked.AddRangeUniqueOnly(fromBeginning);
            return unlocked.Contains(index);
        }

        static void RefreshP2OutfitSprites(int modelIndex)
        {
            if (_p2OutfitGroup?.tabButtons == null) return;
            var character = CharacterManager.GetCharacterFromIndex(modelIndex);
            if (character?.modelPrefab?.outfits == null) return;
            for (int i = 0; i < _p2OutfitGroup.tabButtons.Length; i++)
            {
                var tab = _p2OutfitGroup.tabButtons[i];
                if (tab?.button?.image == null) continue;
                if (i >= character.modelPrefab.outfits.Length || character.modelPrefab.outfits[i] == null) continue;
                tab.button.image.sprite = character.modelPrefab.outfits[i].sprite;
            }
        }

        static void ApplyP2OutfitPreview(int outfitIndex)
        {
            if (_p2CharacterGroup?.tabButtons == null || _p2MenuCharacter == null) return;
            var character = CharacterManager.GetCharacterFromIndex(_p2MenuCharacter.ModelIndex);
            if (character?.modelPrefab?.outfits == null || outfitIndex < 0 || outfitIndex >= character.modelPrefab.outfits.Length) return;
            var tab = Array.Find(_p2CharacterGroup.tabButtons, b => b != null && b.tabIndex == _p2MenuCharacter.ModelIndex) as TabButton_Character;
            if (tab != null && tab.IsInteractable)
                character.modelPrefab.outfits[outfitIndex]?.ApplyOutfit(tab.bodyMesh, null, tab.hairMeshes);
        }

        static void SaveP2CharacterBox()
        {
            if (_p2MenuCharacter == null) return;
            if (_p2CharacterName != null)
                _p2MenuCharacter.Name = string.IsNullOrWhiteSpace(_p2CharacterName.text) ? "P2_GAMEPAD" : _p2CharacterName.text;
            _p2MenuCharacter.Platform = 0;
            P2CharacterSettings.Save(_p2MenuCharacter);
        }
    }
}
