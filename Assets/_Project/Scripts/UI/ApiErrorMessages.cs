using Game.Account;

namespace Game.UI
{
    public static class ApiErrorMessages
    {
        public static string ToUserMessage(ApiResult<object> result)
        {
            if (result == null) return "未知错误，请重试";
            return result.Code switch
            {
                "ECONOMY_INSUFFICIENT_COINS" => "金币不足",
                "SHOP_INSUFFICIENT_COINS" => "金币不足",
                "SHOP_LEVEL_REQUIRED" => "等级不足，先完成更多任务",
                "SHOP_LEVEL_LOCKED" => "等级不足，先完成更多任务",
                "SHOP_ALREADY_OWNED" => "该物品已拥有",
                "SHOP_IDEMPOTENCY_CONFLICT" => "重复请求内容不一致，请刷新商城",
                "LOADOUT_CONFLICT" => "配装已被其他窗口修改，请刷新",
                "LOADOUT_VERSION_CONFLICT" => "配装已被其他窗口修改，请刷新",
                "LOADOUT_ATTACHMENT_INCOMPATIBLE" => "该配件组合尚未适配",
                "ATTACHMENTS_NOT_ADAPTED" => "该武器的配件尚未适配",
                "INVENTORY_NOT_OWNED" => "你尚未拥有该物品",
                "LOADOUT_ITEM_NOT_OWNED" => "你尚未拥有该武器",
                "AUTH_UNAUTHORIZED" => "会话已过期，请重新登录",
                "CLIENT_CANCELLED" => "请求已取消",
                "CLIENT_TIMEOUT" => "后端响应超时，请确认 API 正在运行后重试",
                "CLIENT_CONNECTION_ERROR" => "无法连接后端服务，请确认 API 已启动",
                "CLIENT_DNS_ERROR" => "无法解析后端地址，请检查 API 地址配置",
                "CLIENT_TLS_ERROR" => "安全连接失败，请检查 HTTPS 证书配置",
                "CLIENT_RESPONSE_ERROR" => "服务器响应处理失败，请重试",
                "CLIENT_REQUEST_ERROR" => "客户端无法发起请求，请检查 API 配置",
                "CLIENT_NETWORK_ERROR" => "网络不可用，请检查连接后重试",
                _ => string.IsNullOrWhiteSpace(result.Message) ? "请求失败，请重试" : result.Message
            };
        }

        public static string ToUserMessage<T>(ApiResult<T> result)
        {
            if (result == null) return "未知错误，请重试";
            return result.Code switch
            {
                "ECONOMY_INSUFFICIENT_COINS" => "金币不足",
                "SHOP_INSUFFICIENT_COINS" => "金币不足",
                "SHOP_LEVEL_REQUIRED" => "等级不足，先完成更多任务",
                "SHOP_LEVEL_LOCKED" => "等级不足，先完成更多任务",
                "SHOP_ALREADY_OWNED" => "该物品已拥有",
                "SHOP_IDEMPOTENCY_CONFLICT" => "重复请求内容不一致，请刷新商城",
                "LOADOUT_CONFLICT" => "配装已被其他窗口修改，请刷新",
                "LOADOUT_VERSION_CONFLICT" => "配装已被其他窗口修改，请刷新",
                "LOADOUT_ATTACHMENT_INCOMPATIBLE" => "该配件组合尚未适配",
                "ATTACHMENTS_NOT_ADAPTED" => "该武器的配件尚未适配",
                "INVENTORY_NOT_OWNED" => "你尚未拥有该物品",
                "LOADOUT_ITEM_NOT_OWNED" => "你尚未拥有该武器",
                "AUTH_UNAUTHORIZED" => "会话已过期，请重新登录",
                "CLIENT_CANCELLED" => "请求已取消",
                "CLIENT_TIMEOUT" => "后端响应超时，请确认 API 正在运行后重试",
                "CLIENT_CONNECTION_ERROR" => "无法连接后端服务，请确认 API 已启动",
                "CLIENT_DNS_ERROR" => "无法解析后端地址，请检查 API 地址配置",
                "CLIENT_TLS_ERROR" => "安全连接失败，请检查 HTTPS 证书配置",
                "CLIENT_RESPONSE_ERROR" => "服务器响应处理失败，请重试",
                "CLIENT_REQUEST_ERROR" => "客户端无法发起请求，请检查 API 配置",
                "CLIENT_NETWORK_ERROR" => "网络不可用，请检查连接后重试",
                _ => string.IsNullOrWhiteSpace(result.Message) ? "请求失败，请重试" : result.Message
            };
        }
    }
}

