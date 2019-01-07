namespace Bootleg.API.Model
{
    public class Music
    {
        public string title { get; set; }
        public string path { get; set; }
        public string caption { get; set; }
        public string duration { get; set; }
        public string url { get; set; }
        public bool IsPlaying { get; internal set; }
        public bool IsBuffered { get; internal set; }

        public override bool Equals(object obj)
        {
            if (obj == null)
                return false;

            return this.url == (obj as Music).url;
        }
    }
}