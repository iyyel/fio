(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace FIO.Lib.Network

open System
open System.IO
open System.Net.Sockets
open System.Text.Json
open System.Text.Json.Serialization
open FIO.Core

module Socket =

    type SocketChannel<'T>(socket: Socket) =
        let networkStream = new NetworkStream(socket)
        let reader = new StreamReader(networkStream)
        let writer = new StreamWriter(networkStream)
        let channel = Channel<string>()

        do writer.AutoFlush <- true

        let options = JsonFSharpOptions.Default().ToJsonSerializerOptions()

        member this.Send(message: 'T) : FIO<unit, exn> =
            fio {
                try
                    let serialized = JsonSerializer.Serialize(message, options)
                    do! !+ writer.WriteLine(serialized)
                with exn ->
                    return! !- exn
            }

        member this.Receive() : FIO<'T, exn> =
            let sendToChannel =
                fio {
                    try
                        let! line = !+ reader.ReadLine()

                        if isNull line then
                            return! !- (Exception "Connection closed")
                        else
                            return! channel.Send line
                    with ex ->
                        return! !- ex
                }

            fio {
                do! !!! sendToChannel
                let! data = channel.Receive()
                return JsonSerializer.Deserialize<'T>(data, options)
            }

        member this.Close() : FIO<Unit, exn> =
            fio {
                try
                    do! !+ socket.Close()
                with exn ->
                    return! !- exn
            }
