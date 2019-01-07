/* Copyright (C) 2014 Newcastle University
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 */
using System;
using System.Net;
using System.Threading.Tasks;

namespace AsyncProgressReporting.Common
{
	public class DownloadHelper
	{
		public static readonly int BufferSize = 4096;

        public static async Task<int> CreateDownloadTask(string urlToDownload, IProgress<DownloadBytesProgress> progessReporter)
        {
	        int receivedBytes = 0;
	        int totalBytes = 0;
	        WebClient client = new WebClient();

	        using (var stream = await client.OpenReadTaskAsync(urlToDownload))
	        {
		        byte[] buffer = new byte[BufferSize];
		        totalBytes = Int32.Parse(client.ResponseHeaders[HttpResponseHeader.ContentLength]);

		        for (;;)
		        {
			        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
			        if (bytesRead == 0)
			        {
				        await Task.Yield();
				        break;
			        }

			        receivedBytes += bytesRead;
			        if (progessReporter != null)
			        {
				        DownloadBytesProgress args = new DownloadBytesProgress(urlToDownload, receivedBytes, totalBytes);
				        progessReporter.Report(args);
			        }
		        }
	        }
	        return receivedBytes;
        }
	}
}

