# 05 数据库与 API 设计（原方案 §11）

## 11. Database & API 设计

### 表结构（4 张，刻意克制）

```sql
UserAccount(
  Id           BIGINT PK AUTO,        -- 与 Unity 无关的自增id
  Username     VARCHAR(32) UNIQUE NOT NULL,
  PasswordHash VARCHAR(100) NOT NULL,  -- BCrypt(自带盐, 无需单独Salt列)
  CreatedAt    DATETIME, LastLoginAt DATETIME )

PlayerProfile(
  UserId       BIGINT PK FK->UserAccount.Id,
  Level INT DEFAULT 1, Xp INT DEFAULT 0, SkillPoints INT DEFAULT 0,
  UpDamage INT DEFAULT 0,   -- 三项升级等级 0..5
  UpAmmoCap  INT DEFAULT 0,
  UpMaxHealth INT DEFAULT 0,
  UpdatedAt DATETIME )

PlayerLoadout(
  Id BIGINT PK AUTO,
  UserId BIGINT FK UNIQUE,
  PrimaryWeaponId VARCHAR(32),  -- 引用 Lua/配置中的 weapon key, 不做DB外键
  SecondaryWeaponId VARCHAR(32), ThrowableId VARCHAR(32),
  UpdatedAt DATETIME )

MatchRecord(  -- 可选, Day 12+ 有余力再加
  Id BIGINT PK AUTO, UserId BIGINT FK,
  Kills INT, Deaths INT, Score INT, XpEarned INT, PlayedAt DATETIME )
```

### API（JWT 鉴权；Swagger 自带）

```
POST /api/auth/register  {username, password}
POST /api/auth/login     → {token, profile}          (GET /api/profile 同构)
PUT  /api/profile/upgrades {upDamage,upAmmoCap,upMaxHealth}   (校验技能点+上限)
GET  /api/loadout | PUT /api/loadout
POST /api/matches        {kills,deaths,score} → {xpEarned, levelUps, skillPoints} 
                          (服务器端按公式表 clamp 防刷)
```

### 结算信任边界（面试讨论点，V1 明确取舍）
- V1：**比赛结束 Host 广播各客户端战绩 → 每个客户端自报自己的 POST /api/matches**，服务器对数值做范围 clamp。
- 坦诚标注：这不防作弊（客户端可伪造战绩），正确做法是 Host 用 server-to-server 密钥上报——Demo 权衡，面试主动讲出这一点比假装安全更加分。
- XP/升级公式放 Lua（GetXpForLevel），两端同源。

### 落地路径（针对后端基础）
1. `dotnet new webapi` + Swagger → 跑通一个 echo 接口（半天）
2. EF Core + Pomelo 连 MySQL，Code-First Migration 建表（半天；比手建表更能讲）
3. 注册/登录 + BCrypt + JWT（1 天内，AI 辅助下模板化程度高）
4. 档案/配装/升级/战绩 CRUD（1 天）
5. Unity 侧 ApiClient 用 UnityWebRequest + JSON（半天）
