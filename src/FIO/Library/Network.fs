(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

namespace FIO.Library.Network

open System
open System.IO
open System.Text
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Net.WebSockets
open System.Threading
open System.Text.Json
open System.Text.Json.Serialization

open FIO.Core

module Sockets =

    type Socket<'R>(socket: Socket) =
        let networkStream = new NetworkStream(socket)
        let reader = new StreamReader(networkStream)
        let writer = new StreamWriter(networkStream)

        do writer.AutoFlush <- true

        let options = JsonFSharpOptions.Default().ToJsonSerializerOptions()

        member this.Send(message: 'R) : FIO<unit, exn> =
            try
                let serialized = JsonSerializer.Serialize(message, options)
                writer.WriteLine(serialized)
                writer.Flush()
                !+ ()
            with exn ->
                !- exn

        member this.Receive() : FIO<'R, exn> =
            try 
                let line = reader.ReadLine()
                !+ JsonSerializer.Deserialize<'R>(line, options)
            with exn ->
                !- exn

        member this.RemoteEndPoint() : FIO<EndPoint, exn> =
            try
                !+ socket.RemoteEndPoint
            with exn ->
                !- exn

        member this.Disconnect(reuseSocket: bool) : FIO<unit, exn> =
            try
                socket.Disconnect(reuseSocket)
                !+ ()
            with exn ->
                !- exn

        member this.AddressFamily : FIO<AddressFamily, exn> =
            !+ socket.AddressFamily

        member this.Close() : FIO<unit, exn> =
            !+ socket.Close()

module WebSockets =

    type ClientWebSocket<'R>(uri: Uri) =
        let clientSocket = new ClientWebSocket()
        let options = JsonFSharpOptions.Default().ToJsonSerializerOptions()

        member this.Connect() : FIO<unit, exn> =
            try
                clientSocket.ConnectAsync(uri, CancellationToken.None)
                |> Async.AwaitTask
                |> Async.RunSynchronously
                !+ ()
            with exn ->
                !- exn

        member this.Send(message: 'R) : FIO<unit, exn> =
            try
                let serialized = JsonSerializer.Serialize(message, options)
                let buffer = Encoding.UTF8.GetBytes(serialized)
                clientSocket.SendAsync(ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None).Wait()
                !+ ()
            with exn ->
                !- exn

        member this.Receive() : FIO<'R, exn> = 
            try
                let buffer = Array.zeroCreate<byte> 1024
                let result = clientSocket.ReceiveAsync(ArraySegment<byte>(buffer), CancellationToken.None).Result
                let serialized = Encoding.UTF8.GetString(buffer, 0, result.Count)
                !+ JsonSerializer.Deserialize<'R>(serialized, options)
            with exn ->
                !- exn

        member this.Close() : FIO<unit, exn> =
            try
                clientSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait()
                !+ ()
            with exn ->
                !- exn

module Http =
    
        type HttpClient() =
            let client = new Http.HttpClient()
    
            member this.Get(url: string) : FIO<string, exn> =
                try
                    let response = client.GetAsync(url).Result
                    !+ response.Content.ReadAsStringAsync().Result
                with exn ->
                    !- exn  
    
            member this.Post(url: string, message: string) : FIO<string, exn> =
                try
                    let response = client.PostAsync(url, new StringContent(message)).Result
                    !+ response.Content.ReadAsStringAsync().Result
                with exn ->
                    !- exn
    
            member this.Put(url: string, message: string) : FIO<string, exn> =
                try
                    let stringContent = new StringContent(message)
                    let response = client.PutAsync(url, stringContent).Result
                    !+ response.Content.ReadAsStringAsync().Result
                with exn ->
                    !- exn

            member this.Delete(url: string) : FIO<string, exn> =
                try
                    let response = client.DeleteAsync(url).Result
                    !+ response.Content.ReadAsStringAsync().Result
                with exn ->
                    !- exn
