using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    /// <summary>Boot 只负责：热更检查（下载/校验，失败回退内置不阻塞）→ 建立持久化 AppRoot → 交给 Lobby 场景。
    /// ★ 时序红线（热更稳定 seam）：CheckAndApplyAsync 必须先于 AppRoot.Ensure()——
    /// AppRoot.Awake 挂 HotUpdateRuntime 时 LuaEnv loader 要用已下载目录（HotFilesRoot）。</summary>
    [DefaultExecutionOrder(100)]
    public sealed class BootEntry : MonoBehaviour
    {
        [SerializeField] private string lobbySceneName = "Lobby";

        private string _error;
        private TMPro.TMP_Text _status;
        private void Awake()
        {
            var existing=GameObject.Find("BootLoading");
            if(existing!=null) { _loadingRoot=existing; _status=existing.transform.Find("LoadingState").GetComponent<TMPro.TMP_Text>(); DontDestroyOnLoad(existing); return; }
            var root=new GameObject("BootLoading",typeof(Canvas),typeof(UnityEngine.UI.CanvasScaler),typeof(UnityEngine.UI.GraphicRaycaster));
            var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=32000;
            var scaler=root.GetComponent<UnityEngine.UI.CanvasScaler>();scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);
            UIComponents.Panel("Background",root.transform,UITheme.BackgroundDeep,Vector2.zero,Vector2.one,0,false);
            UITypography.Text("Brand",root.transform,"LOWPOLY OPS",UITheme.FontHero,UITheme.TextPrimary,new Vector2(.12f,.5f),new Vector2(.88f,.66f),TMPro.TextAlignmentOptions.Center);
            _status=UITypography.Text("LoadingState",root.transform,"正在检查作战资源…",UITheme.FontBody,UITheme.AccentPrimary,new Vector2(.1f,.38f),new Vector2(.9f,.48f),TMPro.TextAlignmentOptions.Center);
            UnityEngine.Object.DontDestroyOnLoad(root);
            _loadingRoot=root;
        }
        private GameObject _loadingRoot;
        private UnityEngine.UI.Button _retry;
        private void Update()
        {
            if(_loadingRoot==null)return;
            if(_retry==null)
            {
                _retry=UIComponents.Button("Retry",_loadingRoot.transform,"重试",UIComponents.ButtonKind.Secondary,new Vector2(.43f,.24f),new Vector2(.57f,.31f));
                _retry.onClick.AddListener(()=>Start());
                if(UnityEngine.EventSystems.EventSystem.current==null)
                {
                    var events=new GameObject("BootEventSystem",typeof(UnityEngine.EventSystems.EventSystem),typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
                    // Keep this in Boot's scene: it must unload before Lobby creates its own
                    // event system, rather than disappear with the persistent loading overlay.
                }
            }
            _retry.gameObject.SetActive(!string.IsNullOrEmpty(_error));_retry.interactable=!_busy;
        }
        private bool _busy;

        private async void Start()
        {
            _busy = true;
            _error = null;
            try
            {
            var release = Game.Core.ClientReleaseEnvironment.Current;
            if(_status!=null) _status.text="正在检查作战资源…";
            var result = await HotUpdateBootstrap.CheckAndApplyAsync();
            if (release?.RequiresReleaseValidation == true && result.Kind != "applied" && result.Kind != "uptodate")
                throw new System.InvalidOperationException("热更新未就绪: " + result.Kind + "。请检查网络或联系测试组织者。");
            AppRoot.Ensure();
            if (!string.IsNullOrWhiteSpace(lobbySceneName) && SceneManager.GetActiveScene().name != lobbySceneName)
            {
                if(_status!=null) _status.text="正在连接作战网络…";
                var loading=SceneManager.LoadSceneAsync(lobbySceneName,LoadSceneMode.Single);
                while(!loading.isDone) await Task.Yield();
            }
            if(_loadingRoot!=null) Destroy(_loadingRoot);
            }
            catch (System.Exception exception) { _error = exception.Message; if(_status!=null) _status.text="加载失败，请重试\n"+_error; Debug.LogError("[Boot] " + _error); }
            finally { _busy = false; }
        }
        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_error)) return;
            GUILayout.BeginArea(new Rect(30, 30, Mathf.Min(Screen.width - 60, 720), 180), GUI.skin.box);
            GUILayout.Label(_error);
            GUI.enabled = !_busy;
            if (GUILayout.Button("重试")) Start();
            GUI.enabled = true;
            GUILayout.EndArea();
        }
    }
}
