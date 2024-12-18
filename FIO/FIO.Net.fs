(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace FIO.Net

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.Json
open FIO.Core

module Socket =
    type SocketChannel<'T> private (socket: Socket) =
        let networkStream = new NetworkStream(socket)
        let reader = new StreamReader(networkStream)
        let writer = new StreamWriter(networkStream)
        let channel = Channel<'T>()

        do writer.AutoFlush <- true

        static member CreateLocalServerSocket<'T>(port) : FIO<SocketChannel<'T>, exn> =
            fio {
                try
                    let! listener = succeed <| new TcpListener(IPAddress.Loopback, port)
                    do! succeed <| listener.Start()
                    return SocketChannel<'T>(listener.AcceptSocket())
                with exn ->
                    return! fail exn
            }

        static member CreateClientSocket<'T>(ip: string, port: int) : FIO<SocketChannel<'T>, exn> =
            fio {
                try
                    let! socket =
                        succeed
                        <| new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)

                    do! succeed <| socket.Connect(ip, port)
                    return SocketChannel<'T>(socket)
                with exn ->
                    return! fail exn
            }

        member this.Send(message: 'T) : FIO<'T, exn> =
            fio {
                try
                    do! succeed <| writer.WriteLine(JsonSerializer.Serialize(message))
                    return message
                with exn ->
                    return! fail exn
            }

        member this.Receive() : FIO<'T, exn> =
            let sendToChannel =
                fio {
                    try
                        let! line = succeed <| reader.ReadLine()

                        if isNull line then
                            return! fail <| Exception "Connection closed"
                        else
                            return! send (JsonSerializer.Deserialize<'T>(line)) channel
                    with ex ->
                        return! fail ex
                }

            sendToChannel <*> receive channel >> fun (_, data) -> succeed data

        member this.Close() : FIO<Unit, exn> =
            fio {
                try
                    do! succeed <| socket.Close()
                with exn ->
                    return! fail exn
            }
