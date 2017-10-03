module SocketFlow

open System
open System.Text

open Akka.Actor
open Akka.Streams.Dsl
open Akkling
open Akkling.Streams

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Akka.Streams

type WsMessage =
    | Text of string
    | Data of byte array
    | Close

let handleWebsocketMessagesImpl (system: ActorSystem)
    (materialize: IMaterializer -> Source<WsMessage, Akka.NotUsed> -> Sink<WsMessage, Akka.NotUsed> -> unit) (ws : WebSocket)
    =
    let materializer = system.Materializer()
    let inputSinkActor, publisher =
        Source.actorRef OverflowStrategy.Fail 1000
        |> Source.toMat Sink.publisher Keep.both
        |> Graph.run materializer
    let inputSource = Source.FromPublisher publisher

    let emptyData = ByteSegment [||]

    // sink for flow that sends messages to websocket
    let sinkBehavior _ (ctx: Actor<_>): obj -> _ =
        function
        | Terminated _ ->
            ws.send Opcode.Close emptyData true |> Async.Ignore |> Async.Start
            ignored ()
        | :? WsMessage as wsmsg ->
            wsmsg |> function
            | Text text ->
                // using pipeTo operator just to wait for async send operation to complete
                ws.send Opcode.Text (Encoding.UTF8.GetBytes(text) |> ByteSegment) true |!> ctx.Self
                ignored()
            | Data bytes ->
                ws.send Opcode.Binary (ByteSegment bytes) true |!> ctx.Self
                ignored()
            | Close ->
                // PoisonPill.Instance |!> ctx.Self
                stop()
        | _ ->
            ignored ()
        
    let sinkActor =
        props <| actorOf2 (sinkBehavior ()) |> (spawn system null) |> retype

    let sink: Sink<WsMessage, Akka.NotUsed> = Sink.ActorRef(untyped sinkActor, PoisonPill.Instance)
    materialize materializer inputSource sink

    fun cx -> 
        socket { 
            let loop = ref true
            while !loop do
                let! msg = ws.read()
                
                match msg with
                | (Opcode.Text, data, true) -> 
                    let str = Encoding.UTF8.GetString data
                    inputSinkActor <! Text str
                    ()
                | (Opcode.Ping, _, _) ->
                    do! ws.send Opcode.Pong emptyData true
                | (Opcode.Close, _, _) ->
                    // this finalizes the Source
                    inputSinkActor <! Close
                    do! ws.send Opcode.Close emptyData true
                    loop := false
                | _ -> ()
        }

let handleWebsocketMessages  (system: ActorSystem) (handler: Flow<WsMessage, WsMessage, Akka.NotUsed>) (ws : WebSocket) =
    let materialize materializer inputSource sink =
        inputSource |> Source.via handler |> Source.runWith materializer sink |> ignore
    handleWebsocketMessagesImpl system materialize ws