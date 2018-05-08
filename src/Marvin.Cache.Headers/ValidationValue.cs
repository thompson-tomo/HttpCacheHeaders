﻿// Any comments, input: @KevinDockx
// Any issues, requests: https://github.com/KevinDockx/HttpCacheHeaders

using System;

namespace Marvin.Cache.Headers
{
    public class ValidationValue
    {
        public ETag ETag { get; }
        public DateTimeOffset LastModified { get; }

        public ValidationValue(ETag eTag, DateTimeOffset lastModified)
        {
            ETag = eTag;
            LastModified = lastModified;
        }
    }
}
