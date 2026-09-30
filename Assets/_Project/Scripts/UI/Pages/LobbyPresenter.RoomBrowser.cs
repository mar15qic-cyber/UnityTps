using System;
using System.Linq;
using Game.Account;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed partial class LobbyPresenter
    {
        private string roomModeFilter = "", roomMapFilter = "";
        private bool roomJoinableOnly;
        private long selectedRoomId;
        private float lastRoomClick;
        private RectTransform browserRows;
        private TMP_Text browserDetail;
        private Button browserJoin;
        private Image browserMapPreview;
        private static string MapLabel(string id)
        {
            var map = HotMapCatalog.Cached.FirstOrDefault(m => m != null && m.mapId == id);
            if (map != null && !string.IsNullOrEmpty(map.contentHash) && !HotSceneLoader.IsBundleReady(map.sceneName)) return map.displayName + "（待下载）";
            if (map != null && map.availability == "preparing") return map.displayName + "（准备中）";
            return !string.IsNullOrEmpty(map?.displayName) ? map.displayName : id switch { "arena"=>"Arena", "map_01"=>"Stackyard", "map_02"=>"Depot 55", "map_03"=>"Ridgeline", "map_04"=>"Training Yard", "map_05"=>"Night Relay", _=>id??"未知地图" };
        }
        // 加入谓词（方案A，FD-0020）：后端 InMatch 补人链路完整（RoomService.L194-238），前端只放行入口；
        // Starting/Returning 仍拒绝（后端 409 兜底），Waiting/InMatch 满员均不可加入。
        private static bool RoomCanJoin(GameRoomDto room) => room != null
            && room.joinedPlayers < room.maxPlayers
            && (room.status == "Waiting" || room.status == "InMatch");

        private void RenderOnlineJoin()
        {
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("OnlineJoinPage");
            StyledText(root, "联机对战", UITheme.FontHero, UITheme.TextPrimary, new Vector2(.03f,.87f), new Vector2(.55f,.97f));
            roomListCaptionText = StyledText(root, "正在同步房间…", UITheme.FontCaption, UITheme.TextMuted, new Vector2(.55f,.9f), new Vector2(.82f,.96f));
            StyledButton(root, "刷新", UIComponents.ButtonKind.Secondary, new Vector2(.85f,.9f), new Vector2(.97f,.96f), () => _ = RenderRoomListAsync(browserRows, onlineJoinRenderId));
            var filters = StyledPanel("Filters", root, UITheme.CardSurface, new Vector2(.03f,.16f), new Vector2(.18f,.84f)).transform;
            StyledText(filters, "筛选", UITheme.FontCardTitle, UITheme.TextPrimary, new Vector2(.08f,.88f),new Vector2(.92f,.97f));
            float y = .74f;
            foreach (var mode in new[] { "", "TDM", "KillRace" })
            {
                var captured = mode;
                StyledButton(filters, mode == "" ? "全部模式" : mode == "TDM" ? "团队竞技" : "击杀竞赛", UIComponents.ButtonKind.Secondary,
                    new Vector2(.06f,y), new Vector2(.94f,y+.09f), () => { roomModeFilter = captured; RenderRoomRows(browserRows); });
                y -= .105f;
            }
            var mapFilter = StyledInput("MapFilter", filters, "筛选地图名称", new Vector2(.06f,.31f),new Vector2(.94f,.41f));
            mapFilter.SetTextWithoutNotify(roomMapFilter);
            mapFilter.onValueChanged.AddListener(value => { roomMapFilter=value; RenderRoomRows(browserRows); });
            Button available = null;
            available = StyledButton(filters, roomJoinableOnly ? "✓ 仅可加入" : "显示全部状态", UIComponents.ButtonKind.Secondary,
                new Vector2(.06f,.15f),new Vector2(.94f,.25f), () => {
                    roomJoinableOnly=!roomJoinableOnly;
                    available.GetComponentInChildren<TMP_Text>().text=roomJoinableOnly ? "✓ 仅可加入" : "显示全部状态";
                    RenderRoomRows(browserRows);
                });
            var search=StyledInput("RoomSearch", root,"搜索房主 / 地图",new Vector2(.20f,.77f),new Vector2(.74f,.84f));
            search.SetTextWithoutNotify(roomSearch);
            search.onValueChanged.AddListener(value=> { roomSearch=value; RenderRoomRows(browserRows); });
            var headings = StyledPanel("Columns",root,UITheme.BackgroundPanel,new Vector2(.20f,.70f),new Vector2(.74f,.76f)).transform;
            RoomCell(headings,"地图",0,.29f); RoomCell(headings,"房主",.29f,.55f); RoomCell(headings,"模式",.55f,.73f); RoomCell(headings,"人数",.73f,.84f); RoomCell(headings,"状态",.84f,1);
            browserRows=TacticalScroll(root,"RoomList",new Vector2(.20f,.16f),new Vector2(.74f,.70f));
            var detail=StyledPanel("RoomDetail",root,UITheme.CardSurface,new Vector2(.76f,.16f),new Vector2(.97f,.84f)).transform;
            var preview=UIComponents.Panel("MapPreview",detail,UITheme.BackgroundPanel,new Vector2(.04f,.59f),new Vector2(.96f,.95f),0,false);
            browserMapPreview=preview.GetComponent<Image>();browserMapPreview.preserveAspect=true;
            browserDetail=StyledText(detail,"选择房间查看详情",UITheme.FontBody,UITheme.TextPrimary,new Vector2(.08f,.08f),new Vector2(.92f,.56f),TextAlignmentOptions.TopLeft);
            StyledButton(root,"返回大厅",UIComponents.ButtonKind.Secondary,new Vector2(.03f,.04f),new Vector2(.18f,.12f),()=>Navigate(LobbyPage.Lobby));
            StyledButton(root,"创建房间",UIComponents.ButtonKind.Secondary,new Vector2(.20f,.04f),new Vector2(.38f,.12f),OpenCreateRoomDialog);
            StyledButton(root,"房间码加入",UIComponents.ButtonKind.Secondary,new Vector2(.40f,.04f),new Vector2(.58f,.12f),OpenRoomCodeDialog);
            browserJoin=StyledButton(root,"加入选中房间",UIComponents.ButtonKind.Primary,new Vector2(.76f,.04f),new Vector2(.97f,.12f),JoinSelectedRoom);
            onlineJoinRenderId++;
            RenderRoomRows(browserRows);
            _=RenderRoomListAsync(browserRows,onlineJoinRenderId);
            _=RunRoomListPollAsync(browserRows,onlineJoinRenderId);
            PlayEnter(root.gameObject);
        }

        private void RoomCell(Transform row,string text,float min,float max)
        {
            var label=StyledText(row,text,UITheme.FontCaption,UITheme.TextPrimary,new Vector2(min+.012f,.08f),new Vector2(max-.012f,.92f),TextAlignmentOptions.Left);
            label.richText=false; label.raycastTarget=false; label.textWrappingMode=TextWrappingModes.NoWrap; label.overflowMode=TextOverflowModes.Ellipsis;
        }
        private void RenderRoomRows(Transform content)
        {
            if(content==null)return;
            var scroll=content.GetComponentInParent<ScrollRect>(); var position=content.childCount==0 ? 1f : scroll.verticalNormalizedPosition;
            ClearChildren(content);
            var rows=cachedRoomRows.Where(r=>(roomModeFilter=="" || r.mode==roomModeFilter)
                && (!roomJoinableOnly || RoomCanJoin(r))
                && MapLabel(r.mapId).IndexOf(roomMapFilter,StringComparison.OrdinalIgnoreCase)>=0
                && (r.leaderUsername+" "+MapLabel(r.mapId)).IndexOf(roomSearch,StringComparison.OrdinalIgnoreCase)>=0).ToArray();
            if(roomListCaptionText!=null)roomListCaptionText.text=$"{rows.Length} 个房间 · 每 3 秒刷新";
            foreach(var room in rows)
            {
                var row=StyledPanel("Room_"+room.roomId,content,room.roomId==selectedRoomId ? UITheme.BackgroundPanel : UITheme.CardSurface,Vector2.zero,Vector2.one);
                row.AddComponent<LayoutElement>().preferredHeight=52;
                row.GetComponent<Image>().raycastTarget=true;
                var button=row.AddComponent<Button>(); button.targetGraphic=row.GetComponent<Image>();
                button.onClick.AddListener(()=>{
                    bool twice=selectedRoomId==room.roomId && Time.unscaledTime-lastRoomClick<.35f;
                    selectedRoomId=room.roomId; lastRoomClick=Time.unscaledTime;
                    RenderRoomRows(content); if(twice)JoinSelectedRoom();
                });
                RoomCell(row.transform,MapLabel(room.mapId),0,.29f); RoomCell(row.transform,room.leaderUsername,.29f,.55f);
                RoomCell(row.transform,room.mode=="TDM"?"团队竞技":"击杀竞赛",.55f,.73f);
                RoomCell(row.transform,$"{room.joinedPlayers}/{room.maxPlayers}",.73f,.84f);
                RoomCell(row.transform,room.status=="Waiting"?(RoomCanJoin(room)?"等待中":"已满员"):room.status=="InMatch"?(RoomCanJoin(room)?"对局中·可补人":"对局中"):"准备/结算",.84f,1);
            }
            if(rows.Length==0) { var empty=StyledText(content,"暂无匹配房间",UITheme.FontBody,UITheme.TextMuted,Vector2.zero,Vector2.one); empty.gameObject.AddComponent<LayoutElement>().preferredHeight=60; }
            var selected=rows.FirstOrDefault(r=>r.roomId==selectedRoomId);
            if(browserMapPreview!=null){browserMapPreview.sprite=selected==null?null:Resources.Load<Sprite>("UI/Maps/"+selected.mapId);browserMapPreview.color=browserMapPreview.sprite==null?Color.clear:Color.white;}
            if(browserDetail!=null)browserDetail.text=selected==null?"选择房间查看详情":$"{MapLabel(selected.mapId)}\n\n房主  {selected.leaderUsername}\n\n{(selected.mode=="TDM"?"团队竞技":"击杀竞赛")}\n\n{selected.killTarget} 杀 / {selected.timeLimitMinutes} 分钟\n\n{selected.joinedPlayers} / {selected.maxPlayers} 人";
            if(browserJoin!=null)browserJoin.interactable=RoomCanJoin(selected);
            Canvas.ForceUpdateCanvases(); scroll.verticalNormalizedPosition=position;
        }
        private void JoinSelectedRoom()
        {
            var selected=cachedRoomRows.FirstOrDefault(r=>r.roomId==selectedRoomId);
            if(selected==null)return;
            if(RoomCanJoin(selected)){_=StartOnlineRoomAsync(selected.roomId.ToString());return;}
            // 拒绝不再静默（FD-0020）：原实现双击/点按钮无任何提示，用户误判"点了没反应"
            if(status!=null) status.text=selected.status switch
            {
                "Starting"=>"该房间正在启动比赛，暂不能加入",
                "Returning"=>"该房间正在结算返房，暂不能加入",
                "Closed"=>"该房间已关闭",
                _=>"该房间已满员",
            };
        }
        private Transform RoomDialog(string title)
        {
            CloseSocialModal();
            socialModal=UIComponents.Panel("RoomDialogBackdrop",canvas.transform,new Color(0,0,0,.72f),Vector2.zero,Vector2.one,0,false);
            socialModal.GetComponent<Image>().raycastTarget=true;
            var panel=StyledPanel("RoomDialog",socialModal.transform,UITheme.BackgroundPanel,new Vector2(.29f,.22f),new Vector2(.71f,.78f)).transform;
            panel.GetComponent<Image>().raycastTarget=true;
            StyledText(panel,title,UITheme.FontCardTitle,UITheme.TextPrimary,new Vector2(.06f,.84f),new Vector2(.8f,.96f));
            StyledButton(panel,"×",UIComponents.ButtonKind.Secondary,new Vector2(.86f,.86f),new Vector2(.96f,.96f),CloseSocialModal);
            return panel;
        }
        private void OpenRoomCodeDialog()
        {
            var panel=RoomDialog("输入房主分享的房间码");
            var code=StyledInput("RoomCode",panel,"六位房间码",new Vector2(.08f,.46f),new Vector2(.92f,.64f));code.characterLimit=6;
            StyledButton(panel,"加入房间",UIComponents.ButtonKind.Primary,new Vector2(.51f,.12f),new Vector2(.92f,.27f),()=>{
                var value=code.text.Trim().ToUpperInvariant(); if(value.Length!=6){status.text="请输入六位房间码";return;}
                CloseSocialModal(); _=StartOnlineRoomAsync(value,true);
            });
        }
        private void OpenCreateRoomDialog()
        {
            var panel=RoomDialog("创建房间");
            string[] MapsForMode(string selectedMode)
            {
                var fromServer=HotMapCatalog.Cached.Where(m=>m!=null && m.modes!=null && m.modes.Contains(selectedMode))
                    .Select(m=>m.mapId).ToArray();
                if(fromServer.Length>0)return fromServer;
                return selectedMode=="TDM" ? new[]{"arena","map_01","map_03","map_04"} : new[]{"arena","map_02","map_05"};
            }
            int MaxForMap(string id) => HotMapCatalog.TryGet(id,out var entry) && entry.maxCapacity>0
                ? entry.maxCapacity : id=="map_01"||id=="map_03"||id=="map_04"?8:16;
            var mode="TDM";var capacity=8;var maps=MapsForMode(mode);
            var mapIndex=0;Button modeButton=null,capacityButton=null,mapButton=null;
            modeButton=StyledButton(panel,"模式：团队竞技",UIComponents.ButtonKind.Secondary,new Vector2(.08f,.64f),new Vector2(.92f,.77f),()=>{
                mode=mode=="TDM"?"KillRace":"TDM";maps=MapsForMode(mode);mapIndex=0;
                capacity=Mathf.Min(capacity,MaxForMap(maps[mapIndex]));
                modeButton.GetComponentInChildren<TMP_Text>().text="模式："+(mode=="TDM"?"团队竞技":"击杀竞赛");
                mapButton.GetComponentInChildren<TMP_Text>().text="地图："+MapLabel(maps[mapIndex]);
                capacityButton.GetComponentInChildren<TMP_Text>().text="人数："+capacity;});
            capacityButton=StyledButton(panel,"人数：8",UIComponents.ButtonKind.Secondary,new Vector2(.08f,.47f),new Vector2(.92f,.60f),()=>{
                var choices=new[]{2,4,8,16}.Where(n=>n<=MaxForMap(maps[mapIndex])).ToArray();
                capacity=choices[(Array.IndexOf(choices,capacity)+1)%choices.Length];
                capacityButton.GetComponentInChildren<TMP_Text>().text="人数："+capacity;});
            mapButton=StyledButton(panel,"地图："+MapLabel(maps[0]),UIComponents.ButtonKind.Secondary,new Vector2(.08f,.30f),new Vector2(.92f,.43f),()=>{
                mapIndex=(mapIndex+1)%maps.Length;capacity=Mathf.Min(capacity,MaxForMap(maps[mapIndex]));
                mapButton.GetComponentInChildren<TMP_Text>().text="地图："+MapLabel(maps[mapIndex]);
                capacityButton.GetComponentInChildren<TMP_Text>().text="人数："+capacity;});
            StyledButton(panel,"创建并进入",UIComponents.ButtonKind.Primary,new Vector2(.51f,.08f),new Vector2(.92f,.22f),()=>{
                CloseSocialModal();_=StartOnlineCreateAsync(new CreateRoomRequest{mode=mode,mapId=maps[mapIndex],maxPlayers=capacity,killTarget=mode=="KillRace"?20:100});});
        }
    }
}
