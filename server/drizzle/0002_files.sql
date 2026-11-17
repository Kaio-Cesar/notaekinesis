CREATE TABLE `files` (
	`id` text PRIMARY KEY NOT NULL,
	`user_id` text NOT NULL,
	`name` text NOT NULL,
	`mime` text NOT NULL,
	`size` integer NOT NULL,
	`created_at` integer NOT NULL,
	FOREIGN KEY (`user_id`) REFERENCES `user`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `files_user_idx` ON `files` (`user_id`);--> statement-breakpoint
ALTER TABLE `items` ADD `file_id` text REFERENCES files(id);--> statement-breakpoint
ALTER TABLE `items` ADD `file_name` text;--> statement-breakpoint
ALTER TABLE `items` ADD `file_mime` text;--> statement-breakpoint
ALTER TABLE `items` ADD `file_size` integer;