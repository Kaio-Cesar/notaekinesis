-- Full-text search over items. A standalone FTS5 table (not external-content)
-- keyed by item_id, because items has a TEXT primary key and its implicit rowid
-- is not stable across VACUUM.
CREATE VIRTUAL TABLE `items_fts` USING fts5(
  item_id UNINDEXED,
  text,
  title,
  description,
  url,
  tokenize = 'unicode61 remove_diacritics 2'
);
--> statement-breakpoint
CREATE TRIGGER `items_fts_ai` AFTER INSERT ON `items` WHEN new.deleted_at IS NULL BEGIN
  INSERT INTO items_fts (item_id, text, title, description, url)
  VALUES (new.id, new.text, coalesce(new.title, ''), coalesce(new.description, ''), coalesce(new.url, ''));
END;
--> statement-breakpoint
CREATE TRIGGER `items_fts_au` AFTER UPDATE ON `items` BEGIN
  DELETE FROM items_fts WHERE item_id = old.id;
  INSERT INTO items_fts (item_id, text, title, description, url)
  SELECT new.id, new.text, coalesce(new.title, ''), coalesce(new.description, ''), coalesce(new.url, '')
  WHERE new.deleted_at IS NULL;
END;
--> statement-breakpoint
CREATE TRIGGER `items_fts_ad` AFTER DELETE ON `items` BEGIN
  DELETE FROM items_fts WHERE item_id = old.id;
END;
