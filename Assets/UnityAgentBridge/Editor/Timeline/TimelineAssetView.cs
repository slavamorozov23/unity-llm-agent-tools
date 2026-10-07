using System;
using UnityEditor;
using UnityEngine.Timeline;

namespace UnityAgentBridge.Editor
{
    // A .playable reads as the Timeline window shows it; editing stays with the timeline-* commands.
    [InitializeOnLoad]
    internal sealed class TimelineAssetView : AssetView
    {
        static TimelineAssetView()
        {
            AssetViews.Register(new TimelineAssetView());
        }

        internal override bool Handles(string path, UnityEngine.Object asset)
        {
            return asset is TimelineAsset;
        }

        internal override JsonText Describe(string path, UnityEngine.Object asset, string property)
        {
            var timeline = (TimelineAsset)asset;
            return new JsonText()
                .Add("Duration", Math.Round(timeline.duration, 3))
                .Add("Frame Rate", timeline.editorSettings.frameRate)
                .Add("Tracks", new JsonText.Raw(TimelineService.TracksJson(timeline, null)));
        }
    }
}
