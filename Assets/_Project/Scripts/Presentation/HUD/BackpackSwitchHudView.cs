using Game.Gameplay.Network;
using Game.Gameplay.Player;
using Game.Gameplay.Settings;
using Game.Gameplay.Combat;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Presentation.HUD
{
    public sealed class BackpackSwitchHudView : MonoBehaviour
    {
        private GameObject _panel, _hint;
        private TMP_Text _feedback, _toast;
        private readonly TMP_Text[] _names = new TMP_Text[5];
        private readonly Image[] _pictures = new Image[5];
        private readonly Image[] _tabs = new Image[3];
        private NetworkCombatAuthority _local;
        private PlayerNetworkAdapter _adapter;
        private NetworkWeaponState _weaponState;
        private InputReader _input;
        private bool _open, _pending;
        private int _preview;
        private float _toastUntil;
        private static readonly Color Surface = new(.085f,.12f,.13f,.98f);
        private static readonly Color Accent = new(.35f,.83f,.72f,1);

        public static void TryMount(Canvas canvas)
        {
            if (canvas == null || FindFirstObjectByType<BackpackSwitchHudView>() != null) return;
            var root = new GameObject("BackpackSwitchHud", typeof(RectTransform));
            root.transform.SetParent(canvas.transform, false);
            root.AddComponent<BackpackSwitchHudView>().Build();
        }
        private void OnDisable() => Close();
        private void OnDestroy()
        {
            if (_local != null) _local.OnBackpackSwitchResult -= HandleResult;
            if (_adapter != null) _adapter.OnOwnerBackpackManifestChanged -= Refresh;
            Game.Gameplay.Menu.GameplayInputGate.SetBackpackUiOpen(false);
        }
        private void Update()
        {
            ResolveLocal();
            var reason = _local != null ? _local.OwnerBackpackEligibility : BackpackSwitchPolicy.DenyReason.NotAlive;
            bool eligible = reason == BackpackSwitchPolicy.DenyReason.None || reason == BackpackSwitchPolicy.DenyReason.PendingShots;
            if (_hint != null) _hint.SetActive(eligible && !_open && CanOpen());
            if (_toast != null) _toast.gameObject.SetActive(Time.unscaledTime < _toastUntil);
            if (_local == null) return;
            if (!_open)
            {
                if (_input != null && _input.BackpacksPressed && CanOpen())
                {
                    if (eligible) Open();
                    else { _toast.text = LocalizedDeny(reason); _toastUntil = Time.unscaledTime + 2f; }
                }
                return;
            }
            if (!CanOpen() || !eligible) { Close(); return; }
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.escapeKey.wasPressedThisFrame || kb[SettingsKeyMap.Get(SettingsKeyMap.Action.Backpacks)].wasPressedThisFrame) { Close(); return; }
            if (kb.digit1Key.wasPressedThisFrame) Request(0);
            else if (kb.digit2Key.wasPressedThisFrame) Request(1);
            else if (kb.digit3Key.wasPressedThisFrame) Request(2);
            Highlight();
        }
        internal static bool CanOpenStatic(bool hasLocalOwner, bool isDead, bool menuOpen, bool chatFocused,
            bool hardLocked)
            => hasLocalOwner && !isDead && !menuOpen && !chatFocused && !hardLocked;

        private bool CanOpen() => CanOpenStatic(_local != null, _local != null && _local.IsDead,
            Game.Gameplay.Menu.GameplayInputGate.MenuOpen, Game.Gameplay.Menu.GameplayInputGate.ChatFocused, Game.Gameplay.Menu.GameplayInputGate.HardLocked);
        private void Open()
        {
            _preview = _weaponState != null ? _weaponState.ActiveBackpackIndex : 0;
            _open = true; _pending = false; _panel.SetActive(true);
            Game.Gameplay.Menu.GameplayInputGate.SetBackpackUiOpen(true);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            _feedback.text = "数字键 1 / 2 / 3 或点击切换    ·    B / ESC 关闭";
            Refresh();
        }
        private void Close()
        {
            _open = false; _pending = false;
            Game.Gameplay.Menu.GameplayInputGate.SetBackpackUiOpen(false);
            if (_panel != null) _panel.SetActive(false);
        }
        private void Request(int index)
        {
            if (!_open || _pending || _adapter == null || _adapter.OwnerBackpackManifest == null) return;
            if (_weaponState != null && index == _weaponState.ActiveBackpackIndex) return;
            if (_local.OwnerBackpackEligibility != BackpackSwitchPolicy.DenyReason.None) { _feedback.text = LocalizedDeny(_local.OwnerBackpackEligibility); return; }
            _pending = true; _feedback.text = "正在切换背包…"; _local.SubmitBackpackSwitch(index);
        }
        private void HandleResult(int index, bool accepted, BackpackSwitchPolicy.DenyReason reason)
        { _pending = false; if (accepted) { _local.GetComponent<ThrowableController>()?.Unequip(); Close(); } else if (_feedback != null) _feedback.text = LocalizedDeny(reason); }
        private void ResolveLocal()
        {
            if (_local != null) return;
            foreach (var player in FindObjectsByType<NetworkCombatAuthority>(FindObjectsSortMode.None))
            {
                if (!player.IsOwnerPlayer) continue;
                _local = player; _local.OnBackpackSwitchResult += HandleResult;
                _adapter = player.GetComponentInParent<PlayerNetworkAdapter>();
                _weaponState = player.GetComponent<NetworkWeaponState>();
                _input = player.GetComponentInParent<InputReader>();
                if (_adapter != null) _adapter.OnOwnerBackpackManifestChanged += Refresh;
                break;
            }
        }
        private void Refresh()
        {
            var entries = _adapter != null ? _adapter.OwnerBackpackManifest : null;
            for (int i = 0; i < 5; i++)
            {
                var id = entries != null && entries.Length >= 15 ? entries[_preview * 5 + i] : null;
                _names[i].text = entries == null ? "配装加载中" : i < 2 ? WeaponDisplayName(id) : ThrowableSlots.DisplayName(id);
                _pictures[i].sprite = string.IsNullOrEmpty(id) ? null : Resources.Load<Sprite>("UI/WeaponIcons/" + id);
                _pictures[i].enabled = _pictures[i].sprite != null;
            }
            Highlight();
        }
        private void Highlight()
        {
            int active = _weaponState != null ? _weaponState.ActiveBackpackIndex : -1;
            for (int i=0;i<3;i++) _tabs[i].color = i == _preview ? new Color(.20f,.40f,.36f,1) : i == active ? new Color(.28f,.24f,.17f,1) : new Color(.14f,.19f,.20f,1);
        }
        private static string LocalizedDeny(BackpackSwitchPolicy.DenyReason reason) => reason switch
        {
            BackpackSwitchPolicy.DenyReason.NotInZone => "返回可切换背包的区域后按 B",
            BackpackSwitchPolicy.DenyReason.LockedByFire => "本次生命已开火，复活后可切换背包",
            BackpackSwitchPolicy.DenyReason.LockedByLeaving => "本次生命已离开出生区，复活后可切换",
            BackpackSwitchPolicy.DenyReason.PendingShots => "当前操作尚未完成，请稍后重试",
            BackpackSwitchPolicy.DenyReason.NoBackpackData => "配装加载中，请稍后重试",
            _ => "当前无法切换背包"
        };
        internal static string FormatDeny(BackpackSwitchPolicy.DenyReason reason) => reason switch
        {
            BackpackSwitchPolicy.DenyReason.NotAlive => "ALIVE ONLY",
            BackpackSwitchPolicy.DenyReason.NotInMatch => "MATCH NOT RUNNING",
            BackpackSwitchPolicy.DenyReason.AlreadyActive => "ALREADY EQUIPPED",
            BackpackSwitchPolicy.DenyReason.LockedByFire => "LOCKED — FIRED THIS LIFE",
            BackpackSwitchPolicy.DenyReason.LockedByLeaving => "LOCKED — LEFT SPAWN AREA",
            BackpackSwitchPolicy.DenyReason.NotInZone => "SPAWN AREA ONLY",
            BackpackSwitchPolicy.DenyReason.PendingShots => "BUSY — TRY AGAIN",
            BackpackSwitchPolicy.DenyReason.NoBackpackData => "NO LOADOUT DATA",
            _ => "SWITCH DENIED",
        };

        internal static string WeaponDisplayName(string itemId)
            => string.IsNullOrEmpty(itemId) ? "—"
                : BackpackDisplayBridge.WeaponDisplayName(itemId);

        internal static string ThrowableDisplayName(string itemId) => itemId switch
        {
            "throwable.standard" => "STANDARD PACK",
            "throwable.frag_assault" => "FRAG ASSAULT",
            "" => "NO THROWABLE",
            _ => itemId,
        };

        private void Build()
        {
            Place((RectTransform)transform,0,1,0,1);
            _panel = SurfaceImage("EquipmentPanel",transform,Surface,.23f,.77f,.22f,.78f).gameObject;
            _panel.AddComponent<Outline>().effectColor = new Color(.40f,.48f,.47f,1);
            Label("Title",_panel.transform,"选择背包",28,.03f,.97f,.89f,.98f,TextAlignmentOptions.Center);
            for(int i=0;i<3;i++)
            {
                int index=i;
                var tab=SurfaceImage("BackpackTab"+(i+1),_panel.transform,Surface,.035f+i*.31f,.325f+i*.31f,.77f,.875f);
                _tabs[i]=tab; tab.raycastTarget=true;
                var button=tab.gameObject.AddComponent<Button>(); button.onClick.AddListener(()=>Request(index));
                Label("Label",tab.transform,"背包 "+(i+1),22,0,1,0,1,TextAlignmentOptions.Center);
                var trigger=tab.gameObject.AddComponent<EventTrigger>();
                var enter=new EventTrigger.Entry { eventID=EventTriggerType.PointerEnter }; enter.callback.AddListener(_=>{_preview=index;Refresh();}); trigger.triggers.Add(enter);
            }
            for(int i=0;i<5;i++)
            {
                bool weapon=i<2;
                float x0=weapon?.035f+i*.48f:.035f+(i-2)*.32f;
                float x1=x0+(weapon?.45f:.29f);
                var cell=SurfaceImage("Equipment"+i,_panel.transform,new Color(.055f,.08f,.085f,1),x0,x1,weapon?.37f:.12f,weapon?.735f:.34f);
                Label("Slot",cell.transform,i==0?"主武器":i==1?"副武器":"投掷物 "+(i-1),14,.04f,.96f,.82f,.98f);
                _names[i]=Label("Name",cell.transform,"配装加载中",weapon?20:17,.04f,.96f,.62f,.83f);
                _pictures[i]=SurfaceImage("Model",cell.transform,Color.white,.06f,.94f,.05f,.62f); _pictures[i].preserveAspect=true;
            }
            _feedback=Label("Feedback",_panel.transform,"",16,.03f,.97f,.01f,.09f,TextAlignmentOptions.Center);
            _panel.SetActive(false);
            _hint=SurfaceImage("BackpackAvailable",transform,new Color(0,0,0,.24f),.035f,.092f,.47f,.58f).gameObject;
            var bag=SurfaceImage("BagOutline",_hint.transform,new Color(.72f,.83f,.80f,.9f),.19f,.81f,.32f,.83f);
            SurfaceImage("BagInner",bag.transform,Surface,.055f,.945f,.055f,.945f);
            SurfaceImage("Handle",_hint.transform,new Color(.72f,.83f,.80f,.9f),.35f,.65f,.84f,.9f);
            SurfaceImage("Pocket",_hint.transform,new Color(.45f,.62f,.56f,.9f),.31f,.69f,.42f,.57f);
            Label("Key",_hint.transform,"B  背包",15,0,1,0,.28f,TextAlignmentOptions.Center);
            _hint.SetActive(false);
            _toast=Label("BackpackToast",transform,"",17,.03f,.35f,.42f,.47f);
            _toast.gameObject.SetActive(false);
        }
        private static TMP_Text Label(string name,Transform parent,string value,int size,float xmin,float xmax,float ymin,float ymax,TextAlignmentOptions align=TextAlignmentOptions.Left)
        {
            var go=new GameObject(name,typeof(RectTransform)); go.transform.SetParent(parent,false);
            var text=go.AddComponent<TextMeshProUGUI>();
            var font=Resources.Load<TMP_FontAsset>("Fonts/NotoSansSC-Regular SDF");
#if UNITY_EDITOR
            if(font==null) font=UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset");
#endif
            if(font!=null) text.font=font;
            text.text=value; text.fontSize=size; text.color=new Color(.91f,.94f,.93f,1);text.alignment=align;text.raycastTarget=false;
            text.enableAutoSizing=true;text.fontSizeMin=size*.75f;text.fontSizeMax=size;text.overflowMode=TextOverflowModes.Overflow;
            Place(text.rectTransform,xmin,xmax,ymin,ymax);return text;
        }
        private static Image SurfaceImage(string name,Transform parent,Color color,float xmin,float xmax,float ymin,float ymax)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Image));go.transform.SetParent(parent,false);
            var image=go.GetComponent<Image>();image.color=color;image.raycastTarget=false;Place(image.rectTransform,xmin,xmax,ymin,ymax);return image;
        }
        private static void Place(RectTransform rt,float xmin,float xmax,float ymin,float ymax)
        {rt.anchorMin=new Vector2(xmin,ymin);rt.anchorMax=new Vector2(xmax,ymax);rt.offsetMin=Vector2.zero;rt.offsetMax=Vector2.zero;}
    }
}
