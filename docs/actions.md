# Actions

[← Back to the README](../README.md)

Version 1.2.0 registers **74 actions** (`describe` gives their schemas):

| Area | Actions |
| --- | --- |
| Panels | `modal.close` (`panel` closes an open item panel that does not cover the store, such as `panel:scratch`) |
| Day | `day.open`, `day.close` (`skip_queue`), `day.sleep`, `night.open`, `night.service` |
| Customers | `customer.next`, `customer.dismiss`, `customer.report`, `customer.aim`, `customer.shoot`, `customer.lower_gun`, `dialogue.advance`, `dialogue.choose` |
| Trading | `negotiation.open`, `trade.buy`, `trade.sell`, `trade.select`, `trade.buy_all`, `trade.sell_all`, `trade.wholesale`, `trade.quote`, `trade.weigh`, `trade.accept`, `trade.confirm`, `bargain.offer`, `appraisal.estimate` |
| Items | `item.inspect`, `item.activate`, `item.open`, `item.examine`, `item.toggle`, `item.configure`, `item.use`, `item.transfer`, `item.move`, `item.rotate` |
| Tools and paper | `surface.equip`, `surface.select`, `surface.cancel_item`, `surface.use_selected`, `surface.select_tool`, `surface.confront_feature`, `surface.select_recipe`, `surface.paper_page`, `surface.paper_click`, `surface.book_flip` |
| Night trips | `night.go_out`, `night.travel`, `night.scavenge`, `night.return` |
| Phone, network and marketplace | `phone.call`, `phone.advance`, `phone.choose`, `phone.hang_up`, `network.buy`, `marketplace.open`, `marketplace.close`, `marketplace.fulfill` |
| Saves | `save.list`, `save.load`, `save.new`, `save.main_menu`, `game.quit` |
| Generic UI | `menu.open`, `ui.click`, `ui.hold`, `ui.set`, `ui.drag`, `ui.scratch`, `ui.scroll`, `ui.capture` |
| Raw input | `input.gesture`, `input.cancel` |

Actions call the game's own handlers, so they spend credits, move items and save exactly as the player's moves would. An action is answered once what it set off has played out (a line typed, a customer walking in or out, a fade, a scene load), at most about seven seconds later, so its result shows the consequences: `effects` (`cash_change`, `gained` with name, count, new item IDs and place, `lost`, `reputation_change`, and for `night.*`/`day.*` actions `moved`: your items the game put elsewhere), `talk` (dialogue lines logged meanwhile, the conversation state, its choices), `modal_opened`, `panel_opened` (item panels that opened without covering the store), `notices`, phone actions' `phone`, and `flow` as it is then. A refused action returns an error code such as `precondition_failed` with the `flow` it was judged in. `item.transfer` turns an item to fit the free space when it does not fit as it lies. `item.inspect` and `item.transfer` take `item_ids` for several items in one call; `item.inspect` leaves out screen geometry unless `geometry: true`. UI control IDs stay valid across actions until the control is destroyed or the next observation.

Exercised in long live play (days 20–43+ of one run): the full day cycle with nightly services, rent, buying and selling with bargaining and wholesale, intel purchases, wanted reports, shooting augmented customers, appraisals, upgrades through `network.buy`, landlord and loan dialogues, `save.load`, `game.quit` and `ui.capture`. Other actions are implemented and were exercised during earlier interface work, but not in long play.

**Screen capture:** `ui.capture` (optionally with a UI `path`) reads the frame buffer and writes `captures/<id>.png` to the bridge folder; the action answers once the file is written, and the MCP server returns the picture as an image block alongside the result. It exists for image-only documents and scenes, not as the main interface.

**Raw input:** every input reader in the game's `Assembly-CSharp` (106 methods) is redirected to a virtual input layer, so `input.gesture` drives native hover, drag, key and pointer logic frame by frame without OS input. Steps hold `pointer`, `keys`, `buttons`, `scroll`, `axes` and `named_buttons` for `frames` frames; `input.coverage` reports what was patched. Input fields take text through `ui.set`.
