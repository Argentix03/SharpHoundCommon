﻿namespace SharpHoundCommonLib.OutputTypes
{
    public class RootCA : OutputBase
    {
        public string CertThumbprint { get; set; }
        public Certificate Certificate { get; set; }
    }
}