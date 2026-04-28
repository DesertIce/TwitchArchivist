using System;
using System.Reflection;

var path = @"C:\Users\DesertIce\.nuget\packages\twitchlib.eventsub.websockets\0.8.0\lib\netstandard2.0\TwitchLib.EventSub.Websockets.dll";
var asm = Assembly.LoadFrom(path);
var eventSubType = asm.GetType("TwitchLib.EventSub.Websockets.EventSubWebsocketClient", throwOnError: true)!;
var websocketType = asm.GetType("TwitchLib.EventSub.Websockets.Client.WebsocketClient", throwOnError: true)!;
Console.WriteLine("EventSub fields:");
foreach (var f in eventSubType.GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)) Console.WriteLine($"{f.FieldType.FullName} {f.Name}");
Console.WriteLine("Websocket fields:");
foreach (var f in websocketType.GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)) Console.WriteLine($"{f.FieldType.FullName} {f.Name}");
Console.WriteLine("Websocket methods:");
foreach (var m in websocketType.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly)) Console.WriteLine(m.ToString());
