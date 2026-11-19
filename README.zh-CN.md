<div align="center">

<img src="workshop/preview.png" width="180" alt="Probably Not Human">

# Probably Not Human

让 AI 帮你看店 ——[《深空当铺：可能是偷的》](https://store.steampowered.com/app/4348910/)的 AI 接入模组

[English](README.md) | **简体中文**

</div>

Probably Not Human 是《深空当铺：可能是偷的》（Probably Stolen）的一个模组，附带一个 MCP Server。装上之后，Claude Code、Codex、Cursor 这类 AI 工具就能直接读取游戏状态、操作游戏，自己叫客、鉴定、砍价、买卖、半夜出门拾荒、交房租。

> [!WARNING]
> AI 是在你的真存档上玩的，会花钱、会卖货、会存档。建议专门给它留一个存档位。

## 功能

- 74 个操作，覆盖整个游戏流程：开店关店、叫客对话、交易砍价、物品和工具、夜间外出、电话、存读档
- 游戏状态以 JSON 返回：物品及其真实价值、所有库存、顾客和预算、砍价成功率、日历、新闻、各种文档的内容
- 支持按条件等待（顾客到了、对话结束、到第 12 天……），不用 sleep，也不用轮询
- 没有专门操作的地方，可以用截图加模拟键鼠
- 支持所有 MCP 客户端，也可以用命令行，或者当 Python 库来用
- 纯本地运行，模组不联网，也不需要任何模组权限
- 客户端纯 Python 实现，零依赖

## 环境要求

- Windows 版《深空当铺：可能是偷的》（在 `100-Rev13` 版本上测试）
- Python 3.10+
- 支持 MCP 的 AI 客户端，比如 Claude Code、Codex、Claude Desktop、Cursor；或者任何能执行命令行的 AI 工具

## 安装

1. 在 Steam 创意工坊订阅 [Probably Not Human](https://steamcommunity.com/sharedfiles/filedetails/?id=3842466338)，进游戏打开「改装件」，激活后重启游戏。

2. 安装客户端：

   ```sh
   pip install https://github.com/TundraWork/ProbablyNotHuman/archive/refs/heads/main.zip
   ```

3. 启动游戏，检查能不能连上：

   ```sh
   probably-not-human health
   ```

4. 在 AI 客户端里添加 MCP Server。以 Claude Code 为例：

   ```sh
   claude mcp add probably-stolen -- probably-not-human mcp
   ```

   其他客户端的启动命令是 `probably-not-human`，参数是 `mcp`。各客户端的配置示例、WSL 下的用法和常见问题见 [docs/setup.md](docs/setup.md)。

## 使用

直接让 AI 开玩就行，比如：

> 用 probably-stolen 工具玩《深空当铺》。读取 3 号存档，开一天店，然后告诉我情况怎么样。不要动其他存档。

也可以自己写 Python 脚本：

```python
from probably_not_human import Client

client = Client()
client.act("day.open")
client.wait({"phase": "OPEN"}, timeout=30)
client.act("customer.next")
print(client.observe(["trade"], compact=True))
```

## 注意事项

- AI 能看到的信息比玩家多，物品真实价值、顾客预算、砍价成功率都在里面。
- 模组本身不带任何策略，也不会给 AI 提示，玩得好不好全看模型。
- 客户端和模组通过游戏 `ModData` 目录下的文件通信。模组启用期间，电脑上的其他程序也能读写这个目录。
- 游戏更新后模组可能会失效，需要等模组更新。

## 文档

以下文档目前只有英文。

| | |
| --- | --- |
| [Setup](docs/setup.md) | 各 MCP 客户端的配置、WSL、常见问题、卸载 |
| [Client](docs/client.md) | 命令行、Python 库、等待条件、MCP Server |
| [Observations](docs/observations.md) | AI 能看到哪些信息 |
| [Actions](docs/actions.md) | 全部 74 个操作及其返回值 |
| [Transport](docs/transport.md) | 客户端和模组之间的通信方式 |
| [Development](docs/development.md) | 编译模组、跑测试、发布到创意工坊 |

## 许可证

[MIT](LICENSE)

本项目与 Questing Goose Studio 无关，不包含任何游戏代码或素材。
