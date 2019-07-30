using System;
namespace Bootleg.API.Exceptions
{
    public class StoriesDisabledException : Exception
    {

        public string Content { get; private set; }
        public StoriesDisabledException(string content)
        {
            Content = content;
        }

        public StoriesDisabledException()
        {
        }
    }
}