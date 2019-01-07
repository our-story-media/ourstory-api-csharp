using System;
using System.Collections.Generic;

namespace Bootleg.API.Model
{
	public class ShootTemplate
	{
		public string id { get; set; }
		public string name { get; set; }
		public int version { get; set; }
		public string description { get; set; }
		public string codename { get; set; }
		public bool original { get; set; }
	}
}
