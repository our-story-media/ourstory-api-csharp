/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using System;

namespace AsyncProgressReporting.Common
{
	public class DownloadBytesProgress
	{
		public DownloadBytesProgress(string fileName, int bytesReceived, int totalBytes)
		{
			Filename = fileName;
			BytesReceived = bytesReceived;
			TotalBytes = totalBytes;
		}

		public int TotalBytes { get; private set; }

		public int BytesReceived { get; private set; }

		public float PercentComplete { get { return (float)BytesReceived / TotalBytes; } }

		public string Filename { get; private set; }

		public bool IsFinished { get { return BytesReceived == TotalBytes; } }
	}
}

