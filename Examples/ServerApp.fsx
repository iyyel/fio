(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

open System
open System.Net
open System.Net.Sockets
open System.Collections.Generic

#load "..\FIO\FIO.Core.DSL.fs"
open FIO.Core.DSL

#load "..\FIO\FIO.Core.CE.fs"
open FIO.Core.CE

#load "..\FIO\FIO.Runtime.Core.fs"
open FIO.Runtime

#load "..\FIO\FIO.Runtime.Advanced.fs"
open FIO.Runtime.Advanced

#load "..\FIO\FIOApp.fs"
open FIO.App

#r "nuget: FSharp.SystemTextJson, 1.3.13"
#load "..\FIO\FIO.Lib.Network.fs"
open FIO.Lib.Network.Socket

type Message =
    | Connection of Username: String
    | Broadcast of Username: string * Content: string
    | PrivateMessage of SenderName: string * ReceiverName: string * Content: string

type ServerApp(ip, port) =
    inherit FIOApp<unit, exn>()

    let clients = Dictionary<string, SocketChannel<Message>>()

    let runServer ip port =
        let handleClient clientSocket =
            let handleMessage message =
                fio {
                    match message with
                    | Connection _ ->
                        return! !- (Exception "Connection message received after connection established?")
                    | Broadcast(userName, content) ->
                        do! !+ printfn($"%s{userName}: %s{content}")
                        return! clients
                        |> Seq.filter (fun pair -> pair.Key <> userName)
                        |> Seq.map _.Value.Send(message)
                        |> Seq.fold (fun acc effect -> acc <!> effect) (! ())
                    | PrivateMessage(sender, receiver, message) -> 
                        return! !+ printfn($"%s{sender} says to %s{receiver}: %s{message}")
                }

            let getUsername (channel: SocketChannel<Message>) =
                fio {
                    let! connectionMessage = channel.Receive()
                    match connectionMessage with
                    | Connection userName -> return userName
                    | _ -> return! !- (Exception "First message was not a new connection message?")
                }

            fio {
                let! channel = !+ SocketChannel<Message>(clientSocket)
                let! username = getUsername channel
                do! !+ printfn($"Server: %s{username} has connected from %A{clientSocket.RemoteEndPoint}")
                do! !+ clients.Add((username, channel))

                try
                    while true do
                        let! message = channel.Receive()
                        do! handleMessage message
                with _ ->
                    printfn $"Client disconnected: %A{clientSocket.RemoteEndPoint}"
                    do! channel.Close()
            }

        fio {
            let! listener = !+ (new TcpListener(ip, port))
            do! !+ listener.Start()
            do! !+ printfn($"Server: Listening on %A{ip}:%i{port}...")

            while true do
                let! clientSocket = !+ listener.AcceptSocket()
                do! !! (handleClient clientSocket) >> ! ()
        }

    override this.effect = runServer ip port

FIOApp.Run(ServerApp(IPAddress.Loopback, 5000).effect)
