using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.UI
{
    public sealed class LobbyNavigator
    {
        private readonly Dictionary<LobbyPage, GameObject> pages = new();
        public LobbyPage Current { get; private set; }
        public event Action<LobbyPage> PageChanged;

        public void Register(LobbyPage page, GameObject view)
        {
            pages[page] = view;
            view.SetActive(false);
        }

        public void Show(LobbyPage page)
        {
            foreach (var entry in pages) entry.Value.SetActive(entry.Key == page);
            Current = page;
            PageChanged?.Invoke(page);
        }

        public bool TryGet(LobbyPage page, out GameObject view) => pages.TryGetValue(page, out view);
    }
}

