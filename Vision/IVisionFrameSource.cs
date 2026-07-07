using System;
using System.Drawing;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// The narrow live-camera seam consumed by windows that need frames but must not own the
    /// camera (the vision-protocols window enqueues its detections here). Implemented by
    /// <see cref="VisionViewControl"/>, which owns the framegrabber and the grab thread.
    /// Jobs run on the GRAB thread against the live HObject frame; results are marshalled to
    /// the UI thread via <see cref="PostFrameBitmap"/>, whose callback takes bitmap ownership.
    /// </summary>
    public interface IVisionFrameSource
    {
        /// <summary>True while the framegrabber is open and streaming.</summary>
        bool IsCameraOpen { get; }

        /// <summary>Current 180° display flip (camera is mounted inverted). Detection jobs run
        /// on the RAW frame; pass this as <c>flip</c> only for "what you see" captures.</summary>
        bool InvertView { get; }

        /// <summary>Enqueues a one-shot job to run against the next grabbed frame, on the grab
        /// thread. The frame is only valid for the duration of the job.</summary>
        void RequestFrame(Action<HObject> job);

        /// <summary>Grab-thread helper: converts the frame to a full-res bitmap (optionally
        /// 180°-flipped) and marshals it to the UI thread. The callback owns the bitmap and
        /// must guard its own window's IsDisposed. Skips silently on a conversion failure.</summary>
        void PostFrameBitmap(HObject frame, bool flip, Action<Bitmap> onUi);

        /// <summary>Convenience: full-res "what you see" capture of the next frame (display
        /// flip applied), delivered to the UI thread. No-op if the camera is closed.</summary>
        void CaptureFullRes(Action<Bitmap> onUi);
    }
}
