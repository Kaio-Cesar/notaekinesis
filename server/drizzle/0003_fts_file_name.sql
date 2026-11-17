-- Make attached file names searchable: they're indexed in the `title` column
-- when the item has no title of its own.
DROP TRIGGER `items_fts_ai`;
--> statement-breakpoint
DROP TRIGGER `items_fts_au`;
--> statement-breakpoint
CREATE TRIGGER `items_fts_ai` AFTER INSERT ON `items` WHEN new.deleted_at IS NULL BEGIN
  INSERT INTO items_fts (item_id, text, title, description, url)
  VALUES (new.id, new.text, coalesce(new.title, new.file_name, ''), coalesce(new.description, ''), coalesce(new.url, ''));
END;
--> statement-breakpoint
CREATE TRIGGER `items_fts_au` AFTER UPDATE ON `items` BEGIN
  DELETE FROM items_fts WHERE item_id = old.id;
  INSERT INTO items_fts (item_id, text, title, description, url)
  SELECT new.id, new.text, coalesce(new.title, new.file_name, ''), coalesce(new.description, ''), coalesce(new.url, '')
  WHERE new.deleted_at IS NULL;
END;
