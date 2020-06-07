﻿using System;

namespace Surveillance.RichPresence
{
    public interface IRichPresence : IDisposable
    {
        int UpdateRate { get; }

        void Init();
        void PollEvents();

        void UpdateActivity(string character, string item, string details);
    }
}