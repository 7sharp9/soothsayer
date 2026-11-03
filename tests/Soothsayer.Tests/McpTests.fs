module Soothsayer.Tests.McpTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit

let private cliDll () =
    let rec findRoot (d: DirectoryInfo) =
        if File.Exists(Path.Combine(d.FullName, "Soothsayer.slnx")) then
            d.FullName
        else
            findRoot d.Parent

    let root = findRoot (DirectoryInfo AppContext.BaseDirectory)
    // BaseDirectory looks like .../tests/Soothsayer.Tests/bin/<Config>/net10.0/
    let config =
        DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent.Name

    Path.Combine(root, "src", "Soothsayer.Cli", "bin", config, "net10.0", "soothsayer.dll")

[<Fact>]
let ``MCP stdio round trip: initialize, tools/list, search_symbols, check_snippet`` () =
    let dll = cliDll ()
    Assert.True(File.Exists dll, "CLI not built: " + dll)
    let psi = ProcessStartInfo("dotnet")
    psi.ArgumentList.Add dll
    psi.ArgumentList.Add "mcp"
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    use p = Process.Start psi

    let send (s: string) =
        p.StandardInput.WriteLine s
        p.StandardInput.Flush()

    let readLine () =
        let t = p.StandardOutput.ReadLineAsync()

        if not (t.Wait(TimeSpan.FromSeconds 120.0)) then
            p.Kill true
            failwith "timed out waiting for the MCP server"

        t.Result
    // Every stdout line must be JSON-RPC: any stray text would break the protocol.
    let readJson () =
        JsonDocument.Parse(readLine ()).RootElement

    try
        send (
            """{"jsonrpc":"2.0","id":1,"method":"initialize","""
            + """"params":{"protocolVersion":"2025-06-18","capabilities":{},"""
            + """"clientInfo":{"name":"test","version":"0"}}}"""
        )

        let init = readJson ()
        Assert.Equal("soothsayer", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString())
        send """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
        send """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""
        let tools = readJson ()

        let names =
            [
                for t in tools.GetProperty("result").GetProperty("tools").EnumerateArray() ->
                    t.GetProperty("name").GetString()
            ]

        for n in
            [
                "list_references"
                "search_symbols"
                "get_signature"
                "assembly_outline"
                "check_snippet"
            ] do
            Assert.Contains(n, names)

        send (
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_symbols","""
            + """"arguments":{"target":"nuget:FSharp.Core@10.1.403","query":"List.map","""
            + """"limit":3}}}"""
        )

        let call = readJson ()

        let text =
            call.GetProperty("result").GetProperty("content").[0].GetProperty("text").GetString()

        Assert.Contains("val map: mapping: ('T -> 'U) -> list: 'T list -> 'U list", text)

        send (
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"check_snippet","""
            + """"arguments":{"target":"nuget:FSharp.Core@10.1.403","""
            + """"code":"let y = List.mapp id [1]"}}}"""
        )

        let chk = readJson ()

        let ctext =
            chk.GetProperty("result").GetProperty("content").[0].GetProperty("text").GetString()

        Assert.Contains("FS0039", ctext)
    finally
        if not p.HasExited then
            p.Kill true
