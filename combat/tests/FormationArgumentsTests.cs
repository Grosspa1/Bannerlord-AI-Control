using System;
using System.Globalization;
using BannerlordCombatBridge;
class FormationParserTests {
 static int count;
 static void Main() {
  foreach(string s in new[]{"0|charge","7|stop","0|hold_fire","0|fire_at_will","0|line","0|shield_wall","2|move|-123.25|54.5","99|move|0|0"}) Check(s,true);
  foreach(string s in new string[]{null,"","0","-1|charge","+1|charge","a|charge","0|retreat","0|ai_on","0|Charge","0|charge|1","0|move","0|move|1","0|move|1|2|3","0|move|NaN|1","0|move|1|Infinity","0|move|1e99|0","0|move|1,5|2","2147483648|stop"}) Check(s,false);
  CultureInfo old=CultureInfo.CurrentCulture;
  try {System.Threading.Thread.CurrentThread.CurrentCulture=new CultureInfo("fr-FR");Check("1|move|1.5|-2.25",true);Check("1|move|1,5|2",false);} finally {System.Threading.Thread.CurrentThread.CurrentCulture=old;}
  Console.WriteLine("PASS: "+count+" formation argument checks; no game engine invoked.");
 }
 static void Check(string s,bool expected) {int index;string order;float x,y;bool accepted=true;try {FormationOrders.Parse(s,out index,out order,out x,out y);}catch(ArgumentException){accepted=false;}if(accepted!=expected)throw new Exception("Mismatch: "+s);count++;}
}
