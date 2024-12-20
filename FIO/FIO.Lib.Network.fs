(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

namespace FIO.Lib.Network

open System.IO
open System.Net.Sockets
open System.Text.Json
open System.Text.Json.Serialization
open FIO.Core
open System.Net

module Socket =

    type SocketChannel<'T>(socket: Socket) =
        let networkStream = new NetworkStream(socket)
        let reader = new StreamReader(networkStream)
        let writer = new StreamWriter(networkStream)

        do writer.AutoFlush <- true

        let options = JsonFSharpOptions.Default().ToJsonSerializerOptions()

        member this.Send(message: 'T) : FIO<unit, exn> =
            try
                let serialized = JsonSerializer.Serialize(message, options)
                writer.WriteLine(serialized)
                writer.Flush()
                ! ()
            with exn ->
                !- exn

        member this.Receive() : FIO<'T, exn> =
            try 
                let line = reader.ReadLine()
                !+ JsonSerializer.Deserialize<'T>(line, options)
            with exn ->
                !- exn

        member this.RemoteEndPoint() : FIO<EndPoint, exn> =
            !+ socket.RemoteEndPoint

        member this.Disconnect(reuseSocket: bool) : FIO<unit, exn> =
            try
                socket.Disconnect(reuseSocket)
                ! ()
            with exn ->
                !- exn

        member this.AddressFamily : FIO<AddressFamily, exn> =
            !+ socket.AddressFamily

        member this.Close() : FIO<Unit, exn> =
            try
                socket.Close()
                ! ()
            with exn ->
                !- exn