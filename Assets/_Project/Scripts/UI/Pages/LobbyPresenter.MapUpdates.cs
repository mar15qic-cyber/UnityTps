using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Game.UI
{
    public sealed partial class LobbyPresenter
    {
        private float nextMapCheck;
        private bool roomEntryPending;
        private CancellationTokenSource mapUpdateCancellation;
        private bool mapCheckPending;
        private void Update()
        {
            if (MapContentUpdater.Busy && currentPage == LobbyPage.Lobby && status != null) status.text = MapContentUpdater.Status;
            if (Time.unscaledTime < nextMapCheck || (currentPage != LobbyPage.Lobby && currentPage != LobbyPage.OnlineJoin) || session == null
                || !session.IsAuthenticated || session.Room != null || roomEntryPending || mapCheckPending || MapContentUpdater.Busy) return;
            nextMapCheck = Time.unscaledTime + 30f;
            _ = CheckMapUpdatesAsync();
        }
        private async Task CheckMapUpdatesAsync()
        {
            if (session?.Room != null || roomEntryPending || mapCheckPending || MapContentUpdater.Busy) return;
            mapCheckPending = true;
            mapUpdateCancellation?.Dispose(); mapUpdateCancellation = new CancellationTokenSource();
            var token = mapUpdateCancellation.Token;
            try
            {
                // Catalog availability must never depend on the download channel succeeding.
                await RefreshMapCatalogAsync();
                if (this == null || token.IsCancellationRequested || session?.Room != null || roomEntryPending
                    || currentPage != LobbyPage.Lobby || !session.IsAuthenticated) return;
                await MapContentUpdater.CheckAsync(token);
                if (this != null && !token.IsCancellationRequested && currentPage == LobbyPage.Lobby
                    && session?.Room == null && !roomEntryPending && status != null) status.text = MapContentUpdater.Status;
            }
            finally { mapCheckPending = false; }
        }
    }
}
