using System;
using System.IO;
using Android.Util;
using Java.IO;
using Java.Util.Zip;

namespace Bootleg.API.Decompress
{

    public class Decompress
    {
        String _zipFile;
        String _location;

        public Decompress(String zipFile, String location)
        {
            _zipFile = zipFile;
            _location = location;
            DirChecker("");
        }

        void DirChecker(String dir)
        {
            var file = new Java.IO.File(_location + dir);

            if (!file.IsDirectory)
            {
                file.Mkdirs();
            }
        }

        public void UnZip()
        {
            try
            {
                var fileInputStream = new FileStream(_zipFile, FileMode.Open);
                var zipInputStream = new ZipInputStream(fileInputStream);
                ZipEntry zipEntry = null;

                while ((zipEntry = zipInputStream.NextEntry) != null)
                {
                    Log.Verbose("Decompress", "UnZipping : " + zipEntry.Name);

                    if (zipEntry.IsDirectory)
                    {
                        DirChecker(zipEntry.Name);
                    }
                    else
                    {
                        var fileOutputStream = new FileOutputStream(_location + zipEntry.Name);

                        for (int i = zipInputStream.Read(); i != -1; i = zipInputStream.Read())
                        {
                            fileOutputStream.Write(i);
                        }

                        zipInputStream.CloseEntry();
                        fileOutputStream.Close();
                    }
                }
                zipInputStream.Close();
            }
            catch (Exception ex)
            {
                Log.Error("Decompress", "UnZip", ex);
            }
        }

    }
}
