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
        private void Update()
        {
            if (MapContentUpdater.Busy && status != null) status.text = MapContentUpdater.Status;
            if (Time.unscaledTime < nextMapCheck || currentPage != LobbyPage.Lobby || session == null
                || !session.IsAuthenticated || session.Room != null || roomEntryPending || MapContentUpdater.Busy) return;
            nextMapCheck = Time.unscaledTime + 30f;
            _ = CheckMapUpdatesAsync();
        }
        private async Task CheckMapUpdatesAsync()
        {
            if (session?.Room != null || roomEntryPending || MapContentUpdater.Busy) return;
            mapUpdateCancellation?.Dispose(); mapUpdateCancellation = new CancellationTokenSource();
            var token = mapUpdateCancellation.Token;
            if (await MapContentUpdater.CheckAsync(token) && !token.IsCancellationRequested)
            {
                var maps = await api.ListMapsAsync(token);
                if (maps.Success && !token.IsCancellationRequested) HotMapCatalog.Store(maps.Data);
            }
            if (this != null && status != null) status.text = MapContentUpdater.Status;
        }
    }
}
